CREATE TABLE IF NOT EXISTS document_search (
                                               doc_id    UUID PRIMARY KEY,
                                               uri       VARCHAR NOT NULL,
                                               search_key VARCHAR NOT NULL,
                                               basename  VARCHAR,
                                               dirname   VARCHAR
);

CREATE UNIQUE INDEX IF NOT EXISTS document_search_uri_idx ON document_search(uri);
CREATE INDEX IF NOT EXISTS document_search_search_idx ON document_search(search_key);
CREATE INDEX IF NOT EXISTS document_search_basename_idx ON document_search(basename);
CREATE INDEX IF NOT EXISTS document_search_dirname_idx ON document_search(dirname);

CREATE OR REPLACE MACRO zero_one(x) AS (
  CASE WHEN MAX(x) OVER () IS NULL OR MAX(x) OVER () = 0 THEN 0 ELSE COALESCE(x,0) / NULLIF(MAX(x) OVER (),0) END
);
                  
CREATE OR REPLACE MACRO combine(bm25n, fuzzn, semn, wb := 0.45, wf := 0.45, ws := 0.10) AS (
  coalesce(wb * bm25n, 0) + coalesce(wf * fuzzn, 0) + coalesce(ws * semn, 0)
);
                  
CREATE OR REPLACE MACRO vss_candidates(qvec_json, top_k) AS TABLE (
SELECT doc_id, cosine_similarity_json(qvec_json, embedding) AS sem
FROM document_embedding
ORDER BY sem DESC
LIMIT CAST(top_k AS BIGINT)
);

CREATE OR REPLACE MACRO file_search(
    keywords,
    question := NULL,
    k := 50,
    max_cand := 5000,
    bm25_weight := 0.45,
    fuzzy_weight := 0.45,
    semantic_weight := 0.10
) AS TABLE (
WITH inputs AS (
    SELECT
        coalesce(keywords, '') AS keywords_raw,
        lower(coalesce(keywords, '')) AS keywords_lc,
        CASE WHEN question IS NULL OR length(trim(question)) = 0 THEN NULL ELSE question END AS question_clean,
        CASE WHEN length(trim(coalesce(keywords, ''))) = 0 THEN TRUE ELSE FALSE END AS keywords_empty,
        -- Dynamic semantic weight: boost when keywords are weak/empty
        CASE
            WHEN length(trim(coalesce(keywords, ''))) = 0 THEN 0.70
            WHEN length(trim(coalesce(keywords, ''))) < 5 THEN 0.30
            ELSE semantic_weight
        END AS effective_sem_weight
),
     score_source AS (
         SELECT
             ds.doc_id,
             ds.uri,
             ds.basename,
             -- Basename boost: prioritize exact filename matches
             -- Note: bm25 values >1.0 survive normalization better in relative ranking
             CASE
                 -- Exact match (with or without extension)
                 WHEN lower(ds.basename) = inp.keywords_lc
                   OR lower(regexp_replace(ds.basename, '\.[^.]*$', '')) = inp.keywords_lc THEN 3.0
                 -- Keyword in basename
                 WHEN position(inp.keywords_lc IN lower(ds.basename)) > 0 THEN 2.0
                 -- Keyword in path
                 WHEN position(inp.keywords_lc IN ds.search_key) > 0 THEN 1.0
                 ELSE 0.0
             END AS bm25,
             match_score(inp.keywords_lc, ds.search_key) AS fuzz
         FROM document_search ds
                  CROSS JOIN inputs inp
         WHERE inp.keywords_empty = FALSE
     ),
     ranked_lex AS (
         SELECT doc_id, uri, bm25, fuzz
         FROM score_source
         ORDER BY coalesce(bm25, 0) DESC, fuzz DESC, length(uri)
         LIMIT CAST(max_cand AS BIGINT)
     ),
     normalized_lex AS (
         SELECT
             doc_id,
             uri,
             zero_one(bm25) AS bm25n,
             zero_one(fuzz) AS fuzzn
         FROM ranked_lex
     ),
     semantic_seed AS (
         SELECT
             CASE
                 WHEN inp.question_clean IS NOT NULL THEN inp.question_clean
                 WHEN inp.keywords_empty THEN NULL
                 ELSE inp.keywords_raw
                 END AS query_text
         FROM inputs inp
     ),
     qv AS (
         SELECT embed_text_json(
                        'Represent this sentence for searching relevant passages: ' || query_text) AS qjson
         FROM semantic_seed
         WHERE query_text IS NOT NULL
     ),
     sem_candidates AS (
         SELECT vc.doc_id, vc.sem
         FROM qv
                  CROSS JOIN vss_candidates(qv.qjson, max_cand) AS vc
     ),
     sem_norm AS (
         -- Enhanced semantic spread: apply power transformation to increase separation
         SELECT doc_id, POWER((sem / NULLIF(MAX(sem) OVER (), 0)), 1.5) AS semn FROM sem_candidates
     ),
     union_ids AS (
         SELECT doc_id FROM normalized_lex
         UNION
         SELECT doc_id FROM sem_candidates
     )
SELECT
    u.doc_id,
    ds.uri,
    COALESCE(lx.bm25n, 0) AS bm25n,
    COALESCE(lx.fuzzn, 0) AS fuzzn,
    sn.semn AS semn,
    -- Path-type penalty: de-rank test files, boost docs for questions
    (combine(
            COALESCE(lx.bm25n, 0),
            COALESCE(lx.fuzzn, 0),
            sn.semn,
            wb := bm25_weight,
            wf := fuzzy_weight,
            ws := (SELECT effective_sem_weight FROM inputs)
        ) * CASE
            WHEN (ds.uri LIKE '%/test%/%' OR ds.uri LIKE '%Test%.cs') THEN 0.7
            WHEN ds.uri LIKE '%/docs/%' AND (SELECT question_clean FROM inputs) IS NOT NULL THEN 1.2
            ELSE 1.0
        END) AS score
FROM union_ids u
         LEFT JOIN normalized_lex lx USING(doc_id)
         LEFT JOIN sem_norm sn USING(doc_id)
         JOIN document_search ds USING(doc_id)
ORDER BY score DESC, length(ds.uri)
LIMIT CAST(k AS BIGINT)
);
