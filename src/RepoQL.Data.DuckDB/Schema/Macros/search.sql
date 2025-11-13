-- Primary search entry point combining lexical, fuzzy, and semantic scorers.
CREATE OR REPLACE MACRO search(
    q,
    mode := 'auto',
    k := 50,
    uri_glob := NULL,
    mime_glob := NULL,
    max_cand := 5000,
    bm25_weight := 0.45,
    fuzzy_weight := 0.35,
    semantic_weight := 0.20
) AS TABLE (
WITH base_params AS (
    -- Normalize caller-provided parameters once so every downstream CTE
    -- references stable scalars instead of macro parameters (avoids binder issues).
    SELECT
        coalesce(trim(q), '')                                    AS raw_query,
        lower(coalesce(mode, 'auto'))                            AS requested_mode,
        CAST(coalesce(k, 50) AS BIGINT)                          AS result_k,
        CAST(coalesce(max_cand, 5000) AS BIGINT)                 AS max_candidates,
        coalesce(bm25_weight, 0.45)                              AS bm25_w,
        coalesce(fuzzy_weight, 0.35)                             AS fuzzy_w,
        coalesce(semantic_weight, 0.20)                          AS base_sem_w,
        NULLIF(trim(uri_glob), '')                               AS uri_glob_filter,
        NULLIF(trim(mime_glob), '')                              AS mime_glob_filter
),
-- Classify the query so the engine can route to the best mix of scorers.
classified AS (
    SELECT
        *,
        lower(raw_query)                                         AS keywords_lc,
        CASE WHEN raw_query = '' THEN TRUE ELSE FALSE END        AS keywords_empty,
        CASE
            WHEN requested_mode <> 'auto' THEN requested_mode
            WHEN raw_query = '' THEN 'auto'
            WHEN raw_query LIKE '%::%' OR raw_query LIKE '%.%' OR raw_query LIKE '%()%' THEN 'symbol'
            WHEN regexp_matches(lower(raw_query), '([a-z0-9_]+\.){2,}[a-z0-9_]+') THEN 'symbol'
            WHEN lower(raw_query) LIKE '% exception%' OR lower(raw_query) LIKE '% at %:%' THEN 'error'
            WHEN length(raw_query) > 160 THEN 'heavy'
            ELSE 'auto'
        END                                                      AS route_mode
    FROM base_params
),
-- Configure weighting + candidate limits based on the inferred route.
config AS (
    SELECT
        *,
        CASE
            WHEN keywords_empty THEN 0.70
            WHEN route_mode = 'symbol' THEN base_sem_w * 0.3
            WHEN route_mode = 'heavy' THEN base_sem_w * 1.5
            WHEN route_mode = 'error' THEN base_sem_w * 1.2
            ELSE base_sem_w
        END                                                      AS effective_sem_weight,
        CASE
            WHEN route_mode = 'heavy' THEN max_candidates * 2
            WHEN route_mode = 'symbol' THEN LEAST(max_candidates, 4000)
            ELSE max_candidates
        END                                                      AS lex_limit,
        CASE
            WHEN route_mode = 'symbol' THEN LEAST(max_candidates, 2000)
            WHEN route_mode = 'heavy' THEN max_candidates * 2
            ELSE max_candidates
        END                                                      AS dense_limit
    FROM classified
),
-- Pre-filter repo_index with normalized URI/mime columns.
filtered_source AS (
    SELECT
        ri.*,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(lower(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
),
filtered AS (
    SELECT fs.*
    FROM filtered_source fs
         JOIN base_params bp_filter ON TRUE
    WHERE (
            bp_filter.uri_glob_filter IS NULL
            OR repoql_glob_match(fs.uri, bp_filter.uri_glob_filter, TRUE, 'file:///') IS TRUE
            OR repoql_glob_match(fs.uri_local, bp_filter.uri_glob_filter, TRUE, NULL) IS TRUE
        )
      AND (
            bp_filter.mime_glob_filter IS NULL
            OR repoql_glob_match(coalesce(fs.mime, ''), bp_filter.mime_glob_filter, TRUE, NULL) IS TRUE
        )
      AND (bp_filter.uri_glob_filter IS NULL OR fs.scope = 'document')
),
-- Lexical scorer (BM25-ish heuristics + fuzzy subsequence score).
score_source AS (
    SELECT
        ri.node_id,
        ri.doc_id,
        CASE
            WHEN cfg.keywords_empty THEN 0.0
            WHEN coalesce(ri.symbol_key, '') = cls.keywords_lc THEN 4.0
            WHEN cls.keywords_lc <> '' AND position(cls.keywords_lc IN coalesce(ri.symbol_key, '')) > 0 THEN 3.2
            WHEN lower(coalesce(ri.basename, '')) = cls.keywords_lc
              OR lower(regexp_replace(coalesce(ri.basename, ''), '\\.[^.]*$', '')) = cls.keywords_lc THEN 3.0
            WHEN position(cls.keywords_lc IN lower(coalesce(ri.basename, ''))) > 0 THEN 2.0
            WHEN position(cls.keywords_lc IN ri.search_key) > 0 THEN 1.0
            ELSE 0.0
        END                                                     AS bm25,
        match_score(cls.keywords_lc, ri.search_key)             AS fuzz
    FROM filtered ri
         CROSS JOIN classified cls
        JOIN config cfg ON TRUE
    WHERE cfg.keywords_empty = FALSE
),
ranked_lex AS (
    SELECT node_id, doc_id, bm25, fuzz, lex_row
    FROM (
        SELECT
            node_id,
            doc_id,
            bm25,
            fuzz,
            ROW_NUMBER() OVER (ORDER BY coalesce(bm25, 0) DESC, fuzz DESC, node_id) AS lex_row
        FROM score_source
    ) ranked
         JOIN config cfg ON TRUE
    WHERE lex_row <= cfg.lex_limit
),
lex_ranked AS (
    SELECT node_id, lex_row AS lex_rank
    FROM ranked_lex
),
normalized_lex AS (
    SELECT
        node_id,
        doc_id,
        zero_one(bm25) AS bm25n,
        zero_one(fuzz) AS fuzzn
    FROM ranked_lex
),
lex_rrf AS (
    SELECT node_id, 1.0 / (60 + lex_rank) AS rrf_lex
    FROM lex_ranked
),
-- Dense scorer (OpenAI-style embedding similarity).
semantic_seed AS (
    SELECT
        CASE
            WHEN cfg.keywords_empty THEN NULL
            ELSE cls.raw_query
        END AS query_text
    FROM classified cls
         JOIN config cfg ON TRUE
),
qv AS (
    SELECT embed_text_json(
                   'Represent this sentence for searching relevant passages: ' || query_text) AS qjson
    FROM semantic_seed
    WHERE query_text IS NOT NULL
),
sem_scored AS (
    SELECT
        ri.node_id,
        ri.doc_id,
        cosine_similarity_json(qv.qjson, ri.embedding) AS sem
    FROM qv
             JOIN filtered ri ON ri.embedding IS NOT NULL
),
sem_top AS (
    SELECT node_id, doc_id, sem, sem_rank
    FROM (
        SELECT
            node_id,
            doc_id,
            sem,
            ROW_NUMBER() OVER (ORDER BY sem DESC, node_id) AS sem_rank
        FROM sem_scored
    ) ranked
             JOIN config cfg ON TRUE
    WHERE sem_rank <= cfg.dense_limit
),
sem_norm AS (
    SELECT
        node_id,
        doc_id,
        POWER((sem / NULLIF(MAX(sem) OVER (), 0)), 1.5) AS semn
    FROM sem_top
),
sem_rrf AS (
    SELECT node_id, 1.0 / (60 + sem_rank) AS rrf_sem
    FROM sem_top
),
-- Union lexical + dense nodes; fall back to recency if both empty.
union_nodes AS (
    SELECT node_id FROM normalized_lex
    UNION
    SELECT node_id FROM sem_norm
),
fallback_nodes AS (
    SELECT node_id
    FROM (
        SELECT
            node_id,
            ROW_NUMBER() OVER (ORDER BY mtime DESC, node_id) AS fallback_row
        FROM filtered
    ) fallback
         JOIN base_params bp ON TRUE
    WHERE fallback_row <= bp.result_k
),
combined_nodes AS (
    SELECT node_id FROM union_nodes
    UNION ALL
    SELECT node_id FROM fallback_nodes
    WHERE NOT EXISTS (SELECT 1 FROM union_nodes)
),
final_nodes AS (
    SELECT node_id AS fn_node_id
    FROM (
        SELECT
            node_id,
            ROW_NUMBER() OVER (ORDER BY node_id) AS final_row
        FROM (
            SELECT DISTINCT node_id
            FROM combined_nodes
        )
    ) final
         JOIN base_params bp ON TRUE
    WHERE final_row <= bp.result_k
),
lex_stats AS (
    SELECT COUNT(*) AS cnt FROM normalized_lex
),
sem_stats AS (
    SELECT COUNT(*) AS cnt FROM sem_norm
)
-- Project the final rows along with diagnostics describing the scoring route.
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
    substr(coalesce(ri.body, ''), 1, 640) AS snippet,
    ri.line_start,
    ri.line_end,
    ri.digest,
    coalesce(lx.bm25n, 0)                 AS bm25_score,
    coalesce(lx.fuzzn, 0)                 AS fuzzy_score,
    coalesce(sn.semn, 0)                  AS dense_score,
    coalesce(rlex.rrf_lex, 0) + coalesce(rsem.rrf_sem, 0) AS rrf,
    combine(
        coalesce(lx.bm25n, 0),
        coalesce(lx.fuzzn, 0),
        coalesce(sn.semn, 0),
        wb := cfg.bm25_w,
        wf := cfg.fuzzy_w,
        ws := cfg.effective_sem_weight
    )                                                   AS score,
    CASE
        WHEN combine(
                 coalesce(lx.bm25n, 0),
                 coalesce(lx.fuzzn, 0),
                 coalesce(sn.semn, 0),
                 wb := cfg.bm25_w,
                 wf := cfg.fuzzy_w,
                 ws := cfg.effective_sem_weight
             ) >= 2 THEN 0.95
        WHEN combine(
                 coalesce(lx.bm25n, 0),
                 coalesce(lx.fuzzn, 0),
                 coalesce(sn.semn, 0),
                 wb := cfg.bm25_w,
                 wf := cfg.fuzzy_w,
                 ws := cfg.effective_sem_weight
             ) >= 1.2 THEN 0.8
        WHEN combine(
                 coalesce(lx.bm25n, 0),
                 coalesce(lx.fuzzn, 0),
                 coalesce(sn.semn, 0),
                 wb := cfg.bm25_w,
                 wf := cfg.fuzzy_w,
                 ws := cfg.effective_sem_weight
             ) >= 0.8 THEN 0.65
        ELSE 0.4
    END                                                AS confidence,
    json_object(
        'route', cls.route_mode,
        'uri_glob_applied', cls.uri_glob_filter IS NOT NULL,
        'mime_glob_applied', cls.mime_glob_filter IS NOT NULL,
        'keywords_empty', cls.keywords_empty
    )                                                  AS boosts_json,
    json_object(
        'route', cls.route_mode,
        'lex_candidates', (SELECT cnt FROM lex_stats),
        'dense_candidates', (SELECT cnt FROM sem_stats),
        'requested_mode', cls.requested_mode
    )                                                  AS explain_json
FROM final_nodes fn
         JOIN filtered ri ON ri.node_id = fn.fn_node_id
         LEFT JOIN normalized_lex lx ON lx.node_id = fn.fn_node_id
         LEFT JOIN sem_norm sn ON sn.node_id = fn.fn_node_id
         LEFT JOIN lex_rrf rlex ON rlex.node_id = fn.fn_node_id
         LEFT JOIN sem_rrf rsem ON rsem.node_id = fn.fn_node_id
         JOIN config cfg ON TRUE
         JOIN classified cls ON TRUE
ORDER BY
    CASE
        WHEN cls.uri_glob_filter IS NOT NULL AND ri.scope = 'document' THEN 0
        WHEN cls.uri_glob_filter IS NOT NULL THEN 1
        WHEN cls.uri_glob_filter IS NULL
             AND coalesce(ri.symbol_key, '') = cls.keywords_lc
             AND cls.keywords_lc <> '' THEN -1
        ELSE 0
    END,
    score DESC,
    length(ri.uri)
);

-- Lightweight "find related documents" helper that uses the same filtering approach.
CREATE OR REPLACE MACRO related(
    seed_uri,
    k := 20,
    mode := 'mixed',
    uri_glob := NULL,
    mime_glob := NULL
) AS TABLE (
WITH base_params AS (
    SELECT
        coalesce(trim(seed_uri), '')                              AS seed,
        CAST(coalesce(k, 20) AS BIGINT)                           AS result_k,
        lower(coalesce(mode, 'mixed'))                            AS requested_mode,
        NULLIF(trim(uri_glob), '')                                AS uri_glob_filter,
        NULLIF(trim(mime_glob), '')                               AS mime_glob_filter
),
seed AS (
    SELECT *
    FROM repo_index
         JOIN base_params bp_seed ON TRUE
    WHERE uri = bp_seed.seed
    LIMIT 1
),
related_source AS (
    SELECT
        ri.*,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(lower(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
         JOIN base_params bp_rs ON TRUE
    WHERE ri.uri <> bp_rs.seed
),
filtered AS (
    SELECT rs.*
    FROM related_source rs
         JOIN base_params bp_filter ON TRUE
    WHERE (
            bp_filter.uri_glob_filter IS NULL
            OR repoql_glob_match(rs.uri, bp_filter.uri_glob_filter, TRUE, 'file:///') IS TRUE
            OR repoql_glob_match(rs.uri_local, bp_filter.uri_glob_filter, TRUE, NULL) IS TRUE
        )
      AND (
            bp_filter.mime_glob_filter IS NULL
            OR repoql_glob_match(coalesce(rs.mime, ''), bp_filter.mime_glob_filter, TRUE, NULL) IS TRUE
        )
),
-- Score candidate documents relative to the provided seed URI.
scored AS (
    SELECT
        f.*,
        CASE
            WHEN seed.embedding IS NOT NULL AND f.embedding IS NOT NULL
                THEN cosine_similarity_json(seed.embedding, f.embedding)
            ELSE NULL
        END AS sim_score,
        match_score(lower(coalesce(seed.symbol_key, seed.search_key, '')), f.search_key) AS bm25_score,
        0.0 AS xref_score
    FROM filtered f
             JOIN seed ON TRUE
),
-- Rank by blended semantic + lexical score for output + RRF.
final AS (
    SELECT
        *,
        coalesce(sim_score, 0) * 0.7 + coalesce(bm25_score, 0) * 0.3 AS score,
        ROW_NUMBER() OVER (
            ORDER BY
                coalesce(sim_score, 0) * 0.7 + coalesce(bm25_score, 0) * 0.3 DESC,
                length(uri),
                uri
        ) AS rel_row,
        1.0 / (10 + ROW_NUMBER() OVER (ORDER BY coalesce(sim_score, 0) DESC, coalesce(bm25_score, 0) DESC, uri)) AS rrf
    FROM scored
),
-- Apply the caller's limit without using LIMIT expressions on macro params.
limited AS (
    SELECT f.*
    FROM final f
             JOIN base_params bp_limit ON TRUE
    WHERE f.rel_row <= bp_limit.result_k
)
SELECT
    doc_id,
    node_id,
    uri,
    path,
    scope,
    kind,
    symbol,
    lang,
    mime,
    headline,
    structure,
    substr(coalesce(body, ''), 1, 640) AS snippet,
    line_start,
    line_end,
    digest,
    bm25_score,
    sim_score AS dense_score,
    xref_score,
    score,
    rrf,
    CASE
        WHEN score >= 1.8 THEN 0.9
        WHEN score >= 1.0 THEN 0.75
        WHEN score >= 0.6 THEN 0.55
        ELSE 0.35
    END AS confidence,
    json_object('mode', bp_out.requested_mode, 'seed_uri', bp_out.seed) AS explain_json
FROM limited
         JOIN base_params bp_out ON TRUE
ORDER BY score DESC, length(uri)
);

CREATE OR REPLACE MACRO file_search(
    keywords,
    question := NULL,
    k := 50,
    max_cand := 5000
) AS TABLE (
SELECT
    doc_id,
    uri,
    bm25_score AS bm25n,
    fuzzy_score AS fuzzn,
    dense_score AS semn,
    score
FROM search(
        trim(concat_ws(' ', coalesce(keywords, ''), coalesce(question, ''))),
        mode := CASE
                  WHEN question IS NOT NULL AND length(trim(question)) > 0 THEN 'heavy'
                  ELSE 'auto'
                END,
        k := k,
        max_cand := max_cand
    )
);
