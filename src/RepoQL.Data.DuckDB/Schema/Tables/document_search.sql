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
SELECT doc_id, node_id, scope, cosine_similarity_json(qvec_json, embedding) AS sem
FROM document_embedding
WHERE scope IN ('document', 'object')
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
repo_base AS (
    SELECT *
    FROM repo_index
),
score_source AS (
    SELECT
        ri.node_id,
        ri.doc_id,
        -- Symbol + path heuristics act like lightweight BM25 surrogates
        CASE
            WHEN inp.keywords_empty THEN 0.0
            WHEN COALESCE(ri.symbol_key, '') = inp.keywords_lc THEN 4.0
            WHEN inp.keywords_lc <> '' AND position(inp.keywords_lc IN COALESCE(ri.symbol_key, '')) > 0 THEN 3.2
            WHEN lower(COALESCE(ri.basename, '')) = inp.keywords_lc
              OR lower(regexp_replace(COALESCE(ri.basename, ''), '\\.[^.]*$', '')) = inp.keywords_lc THEN 3.0
            WHEN position(inp.keywords_lc IN lower(COALESCE(ri.basename, ''))) > 0 THEN 2.0
            WHEN position(inp.keywords_lc IN ri.search_key) > 0 THEN 1.0
            ELSE 0.0
        END AS bm25,
        match_score(inp.keywords_lc, ri.search_key) AS fuzz
    FROM repo_base ri
             CROSS JOIN inputs inp
    WHERE inp.keywords_empty = FALSE
),
ranked_lex AS (
    SELECT node_id, doc_id, bm25, fuzz
    FROM score_source
    ORDER BY coalesce(bm25, 0) DESC, fuzz DESC
    LIMIT CAST(max_cand AS BIGINT)
),
normalized_lex AS (
    SELECT
        node_id,
        doc_id,
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
    SELECT vc.doc_id, vc.node_id, vc.sem
    FROM qv
             CROSS JOIN vss_candidates(qv.qjson, max_cand) AS vc
),
sem_norm AS (
    -- Enhanced semantic spread: apply power transformation to increase separation
    SELECT node_id, doc_id, POWER((sem / NULLIF(MAX(sem) OVER (), 0)), 1.5) AS semn
    FROM sem_candidates
),
union_nodes AS (
    SELECT node_id FROM normalized_lex
    UNION
    SELECT node_id FROM sem_candidates
)
SELECT
    ri.doc_id,
    ri.node_id,
    ri.uri,
    ri.path,
    ri.scope,
    ri.kind,
    ri.symbol,
    ri.lang,
    ri.mime,
    ri.headline,
    ri.structure,
    ri.body,
    ri.line_start,
    ri.line_end,
    ri.digest,
    COALESCE(lx.bm25n, 0) AS bm25n,
    COALESCE(lx.fuzzn, 0) AS fuzzn,
    COALESCE(sn.semn, 0) AS semn,
    (
        combine(
            COALESCE(lx.bm25n, 0),
            COALESCE(lx.fuzzn, 0),
            COALESCE(sn.semn, 0),
            wb := bm25_weight,
            wf := fuzzy_weight,
            ws := (SELECT effective_sem_weight FROM inputs)
        )
        * CASE
              WHEN (ri.path LIKE '%/test%/%' OR ri.path LIKE '%Test%.cs') THEN 0.7
              WHEN ri.path LIKE '%/docs/%' AND (SELECT question_clean FROM inputs) IS NOT NULL THEN 1.2
              ELSE 1.0
          END
        * CASE WHEN ri.scope = 'object' THEN 1.05 ELSE 1.0 END
    ) AS score
FROM union_nodes u
         JOIN repo_base ri ON ri.node_id = u.node_id
         LEFT JOIN normalized_lex lx USING(node_id)
         LEFT JOIN sem_norm sn USING(node_id)
ORDER BY score DESC, length(ri.uri)
LIMIT CAST(k AS BIGINT)
);
