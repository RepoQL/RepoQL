-- Internal search candidates function combining lexical and semantic scorers.
-- This is a slim orchestrator that calls modular components:
--   _search_lexical()  - BM25/fuzzy scoring
--   _search_semantic() - HNSW/linear semantic scoring
-- NOTE: This is an internal helper - use search() (in hybrid_search.sql) for public API.

CREATE OR REPLACE MACRO _search_candidates(
    q,
    mode := 'auto',
    k := 50,
    uri_glob := NULL,
    mime_glob := NULL,
    max_cand := 5000,
    bm25_weight := 0.15,
    fuzzy_weight := 0.15,
    semantic_weight := 0.70
) AS TABLE (
WITH
-- ============================================================================
-- PARAMETER NORMALIZATION
-- ============================================================================
base_params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        LOWER(COALESCE(mode, 'auto')) AS requested_mode,
        CAST(COALESCE(k, 50) AS BIGINT) AS result_k,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS max_candidates,
        COALESCE(bm25_weight, 0.15) AS bm25_w,
        COALESCE(fuzzy_weight, 0.15) AS fuzzy_w,
        COALESCE(semantic_weight, 0.70) AS base_sem_w,
        NULLIF(TRIM(uri_glob), '') AS uri_glob_filter,
        NULLIF(TRIM(mime_glob), '') AS mime_glob_filter
),

-- Query classification for routing and weight adjustment
classified AS (
    SELECT
        *,
        LOWER(raw_query) AS keywords_lc,
        CASE WHEN raw_query = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        _search_classify_query(raw_query) AS route_mode
    FROM base_params
),

-- Weight/limit configuration based on query type
config AS (
    SELECT
        *,
        CASE
            WHEN keywords_empty THEN 0.80
            WHEN route_mode = 'symbol' THEN base_sem_w * 0.7
            ELSE base_sem_w
        END AS effective_sem_weight
    FROM classified
),

-- ============================================================================
-- CALL MODULAR SEARCH COMPONENTS
-- ============================================================================

-- Lexical scoring (BM25 + fuzzy)
lex AS (
    SELECT * FROM _search_lexical(
        (SELECT raw_query FROM base_params),
        (SELECT uri_glob_filter FROM base_params),
        (SELECT mime_glob_filter FROM base_params),
        (SELECT max_candidates FROM base_params)
    )
),

-- Semantic scoring (HNSW when available, linear fallback)
sem AS (
    SELECT * FROM _search_semantic(
        (SELECT raw_query FROM base_params),
        (SELECT uri_glob_filter FROM base_params),
        (SELECT mime_glob_filter FROM base_params),
        (SELECT max_candidates FROM base_params)
    )
),

-- ============================================================================
-- COMBINE SCORES
-- ============================================================================

-- Union all candidate nodes from both scorers
union_nodes AS (
    SELECT node_id FROM lex
    UNION
    SELECT node_id FROM sem
),

-- Fallback to recency if both scorers return nothing
filtered_source AS (
    SELECT
        ri.*,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(LOWER(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
),
filtered AS (
    SELECT fs.*
    FROM filtered_source fs
    JOIN base_params bp ON TRUE
    WHERE (
            bp.uri_glob_filter IS NULL
            OR repoql_glob_match(fs.uri, bp.uri_glob_filter, 'true','file:///') IS TRUE
            OR repoql_glob_match(fs.uri_local, bp.uri_glob_filter, 'true',NULL) IS TRUE
        )
      AND (
            bp.mime_glob_filter IS NULL
            OR repoql_glob_match(COALESCE(fs.mime, ''), bp.mime_glob_filter, 'true',NULL) IS TRUE
        )
),
fallback_nodes AS (
    SELECT node_id
    FROM filtered
    QUALIFY ROW_NUMBER() OVER (ORDER BY mtime DESC, node_id) <= (SELECT result_k FROM base_params)
),
combined_nodes AS (
    SELECT node_id FROM union_nodes
    UNION ALL
    SELECT node_id FROM fallback_nodes
    WHERE NOT EXISTS (SELECT 1 FROM union_nodes)
),
final_nodes AS (
    SELECT DISTINCT node_id AS fn_node_id FROM combined_nodes
),

-- Stats for explain_json
lex_stats AS (SELECT COUNT(*) AS cnt FROM lex),
sem_stats AS (SELECT COUNT(*) AS cnt FROM sem),

-- ============================================================================
-- ENRICH AND SCORE
-- ============================================================================

scored AS (
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
        -- Use semantic chunk location for snippet when available
        CASE
            WHEN s.best_chunk_start IS NOT NULL
                 AND s.best_chunk_end IS NOT NULL
                 AND art.text_content IS NOT NULL
                 AND LENGTH(art.text_content) > 0
            THEN array_to_string(
                list_slice(
                    string_split(art.text_content, chr(10)),
                    GREATEST(1, TRY_CAST(line_for_byte_offset(art.text_content, CAST(s.best_chunk_start AS VARCHAR)) AS INTEGER) - 2),
                    LEAST(
                        len(string_split(art.text_content, chr(10))),
                        TRY_CAST(line_for_byte_offset(art.text_content, CAST(s.best_chunk_end AS VARCHAR)) AS INTEGER) + 2
                    )
                ),
                chr(10)
            )
            -- Fallback: for documents use text_content, for objects use metadata (avoid expensive line extraction)
            WHEN ri.scope = 'document'
            THEN substr(COALESCE(art.text_content, ''), 1, 640)
            ELSE substr(COALESCE(ri.headline || E'\n\n' || ri.structure, ri.headline, ri.structure, ''), 1, 640)
        END AS snippet,
        ri.line_start,
        ri.line_end,
        ri.digest,
        COALESCE(l.bm25_norm, 0) AS bm25_score,
        COALESCE(l.fuzz_norm, 0) AS fuzzy_score,
        COALESCE(s.sem_norm, 0) AS dense_score,
        COALESCE(l.rrf_lex, 0) + COALESCE(s.rrf_sem, 0) AS rrf
    FROM final_nodes fn
    JOIN filtered ri ON ri.node_id = fn.fn_node_id
    LEFT JOIN lex l ON l.node_id = fn.fn_node_id
    LEFT JOIN sem s ON s.doc_id = ri.doc_id
    LEFT JOIN node doc_node ON doc_node.id = ri.doc_id
    LEFT JOIN artifact art ON art.id = doc_node.artifact_id
),

-- Propagate semantic score from document to its objects
doc_sem AS (
    SELECT doc_id, MAX(dense_score) AS doc_semn
    FROM scored
    GROUP BY doc_id
),

-- Final scoring with confidence
final_with_conf AS (
    SELECT
        fws.*,
        score_confidence(fws.score) AS confidence
    FROM (
        SELECT
            s.*,
            COALESCE(ds.doc_semn, s.dense_score) AS doc_semn,
            combine(
                s.bm25_score,
                s.fuzzy_score,
                COALESCE(ds.doc_semn, s.dense_score),
                wb := cfg.bm25_w,
                wf := cfg.fuzzy_w,
                ws := cfg.effective_sem_weight
            ) AS score,
            cls.route_mode,
            cls.uri_glob_filter,
            cls.mime_glob_filter,
            cls.keywords_empty,
            cls.keywords_lc,
            cls.requested_mode,
            json_object(
                'route', cls.route_mode,
                'uri_glob_applied', cls.uri_glob_filter IS NOT NULL,
                'mime_glob_applied', cls.mime_glob_filter IS NOT NULL,
                'keywords_empty', cls.keywords_empty
            ) AS boosts_json,
            json_object(
                'route', cls.route_mode,
                'lex_candidates', (SELECT cnt FROM lex_stats),
                'dense_candidates', (SELECT cnt FROM sem_stats),
                'requested_mode', cls.requested_mode
            ) AS explain_json
        FROM scored s
        LEFT JOIN doc_sem ds ON ds.doc_id = s.doc_id
        JOIN classified cls ON TRUE
        JOIN config cfg ON TRUE
    ) fws
)

-- ============================================================================
-- OUTPUT
-- ============================================================================
SELECT *
FROM final_with_conf
ORDER BY
    CASE
        WHEN uri_glob_filter IS NOT NULL AND scope = 'document' THEN 0
        WHEN uri_glob_filter IS NOT NULL THEN 1
        WHEN uri_glob_filter IS NULL
             AND COALESCE(symbol, '') = COALESCE(keywords_lc, '')
             AND COALESCE(keywords_lc, '') <> '' THEN -1
        ELSE 0
    END,
    score DESC,
    LENGTH(uri)
LIMIT (SELECT result_k FROM base_params)
);

-- ============================================================================
-- RELATED DOCUMENTS HELPER
-- ============================================================================
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
        COALESCE(TRIM(seed_uri), '') AS seed,
        CAST(COALESCE(k, 20) AS BIGINT) AS result_k,
        LOWER(COALESCE(mode, 'mixed')) AS requested_mode,
        NULLIF(TRIM(uri_glob), '') AS uri_glob_filter,
        NULLIF(TRIM(mime_glob), '') AS mime_glob_filter
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
            ELSE regexp_replace(LOWER(ri.uri), '^[^:]+://+', '')
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
            OR repoql_glob_match(rs.uri, bp_filter.uri_glob_filter, 'true','file:///') IS TRUE
            OR repoql_glob_match(rs.uri_local, bp_filter.uri_glob_filter, 'true',NULL) IS TRUE
        )
      AND (
            bp_filter.mime_glob_filter IS NULL
            OR repoql_glob_match(COALESCE(rs.mime, ''), bp_filter.mime_glob_filter, 'true',NULL) IS TRUE
        )
),
scored AS (
    SELECT
        f.*,
        CASE
            WHEN seed.embedding IS NOT NULL AND f.embedding IS NOT NULL
                THEN list_cosine_similarity(seed.embedding, f.embedding)
            ELSE NULL
        END AS sim_score,
        TRY_CAST(match_score(LOWER(COALESCE(seed.symbol_key, seed.search_key, '')), f.search_key) AS DOUBLE) AS bm25_score,
        0.0 AS xref_score
    FROM filtered f
    JOIN seed ON TRUE
),
-- Apply limit via QUALIFY for optimizer early-termination
final AS (
    SELECT
        *,
        COALESCE(sim_score, 0) * 0.7 + COALESCE(bm25_score, 0) * 0.3 AS score,
        ROW_NUMBER() OVER (
            ORDER BY
                COALESCE(sim_score, 0) * 0.7 + COALESCE(bm25_score, 0) * 0.3 DESC,
                LENGTH(uri),
                uri
        ) AS rel_row,
        rrf_score(ROW_NUMBER() OVER (ORDER BY COALESCE(sim_score, 0) DESC, COALESCE(bm25_score, 0) DESC, uri), 10) AS rrf
    FROM scored
    QUALIFY rel_row <= (SELECT result_k FROM base_params)
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
    substr(COALESCE(headline || E'\n\n' || structure, headline, structure, ''), 1, 640) AS snippet,
    line_start,
    line_end,
    digest,
    bm25_score,
    sim_score AS dense_score,
    xref_score,
    score,
    rrf,
    score_confidence(score) AS confidence,
    json_object('mode', bp_out.requested_mode, 'seed_uri', bp_out.seed) AS explain_json
FROM final
JOIN base_params bp_out ON TRUE
ORDER BY score DESC, LENGTH(uri)
);
