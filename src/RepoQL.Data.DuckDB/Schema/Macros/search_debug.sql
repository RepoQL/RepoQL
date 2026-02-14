-- Search debugging and diagnostic macros.
-- Use these to understand search behavior, benchmark components, and verify HNSW usage.

-- Check which search path will be used for semantic search.
-- Returns: query_dim, vss_384_count, vss_768_count, vss_1024_count, search_path
-- Uses embed_query() which handles E5 model prefixes automatically.
CREATE OR REPLACE MACRO _search_semantic_explain(q) AS TABLE (
    WITH
    query_vec AS (
        SELECT embed_query(COALESCE(q, '')) AS vec
        WHERE q IS NOT NULL AND TRIM(q) <> ''
    ),
    dim_info AS (
        SELECT
            COALESCE(array_length(vec::FLOAT[]), 0) AS query_dim
        FROM query_vec
    ),
    vss_counts AS (
        SELECT
            (SELECT COUNT(*) FROM _vss_index_384) AS cnt_384,
            (SELECT COUNT(*) FROM _vss_index_768) AS cnt_768,
            (SELECT COUNT(*) FROM _vss_index_1024) AS cnt_1024,
            COALESCE(
                (
                    SELECT LOWER(TRIM(value)) = 'true'
                    FROM metadata
                    WHERE key = 'vss_structure_ready'
                    LIMIT 1
                ),
                FALSE
            ) AS structure_ready
    )
    SELECT
        di.query_dim,
        vc.cnt_384 AS vss_384_count,
        vc.cnt_768 AS vss_768_count,
        vc.cnt_1024 AS vss_1024_count,
        vc.structure_ready AS vss_structure_ready,
        CASE
            WHEN di.query_dim = 384 AND vc.cnt_384 > 0 AND vc.structure_ready = TRUE THEN 'HNSW_384'
            WHEN di.query_dim = 768 AND vc.cnt_768 > 0 THEN 'HNSW_768'
            WHEN di.query_dim = 1024 AND vc.cnt_1024 > 0 THEN 'HNSW_1024'
            ELSE 'LINEAR_SCAN'
        END AS search_path
    FROM dim_info di, vss_counts vc
);

-- Get VSS index status for all dimensions.
CREATE OR REPLACE MACRO _vss_status() AS TABLE (
    SELECT
        '_vss_index_384' AS index_name,
        384 AS dimension,
        (SELECT COUNT(*) FROM _vss_index_384) AS row_count,
        (SELECT COUNT(DISTINCT embedding_type) FROM _vss_index_384) AS embedding_types
    UNION ALL
    SELECT
        '_vss_index_768',
        768,
        (SELECT COUNT(*) FROM _vss_index_768),
        (SELECT COUNT(DISTINCT embedding_type) FROM _vss_index_768)
    UNION ALL
    SELECT
        '_vss_index_1024',
        1024,
        (SELECT COUNT(*) FROM _vss_index_1024),
        (SELECT COUNT(DISTINCT embedding_type) FROM _vss_index_1024)
);

-- Test HNSW search directly (bypasses search macro).
-- Use this to verify HNSW is working and measure its performance.
-- Uses embed_query() which handles E5 model prefixes automatically.
CREATE OR REPLACE MACRO _search_hnsw_direct(q, k := 10) AS TABLE (
    WITH
    query_vec AS (
        SELECT embed_query(q)::FLOAT[384] AS vec
    )
    SELECT
        v.doc_id,
        v.node_id,
        v.embedding_type,
        1.0 - array_cosine_distance(v.vec, qv.vec) AS score
    FROM query_vec qv, _vss_index_384 v
    WHERE v.embedding_type = 'structure'
    ORDER BY array_cosine_distance(v.vec, qv.vec)
    LIMIT k
);

-- Test linear scan search directly (bypasses search macro).
-- Use this for comparison with HNSW.
-- Uses embed_query() which handles E5 model prefixes automatically.
CREATE OR REPLACE MACRO _search_linear_direct(q, k := 10) AS TABLE (
    WITH
    query_vec AS (
        SELECT embed_query(q)::FLOAT[] AS vec
    )
    SELECT
        de.doc_id,
        de.node_id,
        de.embedding_type,
        list_cosine_similarity(qv.vec, de.embedding) AS score
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    WHERE de.scope = 'document'
      AND de.embedding_type = 'structure'
      AND de.dim = array_length(qv.vec)
    ORDER BY score DESC
    LIMIT k
);

-- Compare HNSW vs linear results for a query.
-- Helps verify HNSW is returning correct results.
CREATE OR REPLACE MACRO _search_compare_paths(q, k := 10) AS TABLE (
    WITH
    hnsw AS (
        SELECT node_id, score, 'HNSW' AS source
        FROM _search_hnsw_direct(q, k)
    ),
    linear AS (
        SELECT node_id, score, 'LINEAR' AS source
        FROM _search_linear_direct(q, k)
    )
    SELECT
        COALESCE(h.node_id, l.node_id) AS node_id,
        h.score AS hnsw_score,
        l.score AS linear_score,
        ABS(COALESCE(h.score, 0) - COALESCE(l.score, 0)) AS score_diff,
        CASE
            WHEN h.node_id IS NOT NULL AND l.node_id IS NOT NULL THEN 'BOTH'
            WHEN h.node_id IS NOT NULL THEN 'HNSW_ONLY'
            ELSE 'LINEAR_ONLY'
        END AS presence
    FROM hnsw h
    FULL OUTER JOIN linear l ON h.node_id = l.node_id
    ORDER BY COALESCE(h.score, l.score) DESC
);

-- Get embedding statistics for diagnostics.
CREATE OR REPLACE MACRO _embedding_stats() AS TABLE (
    SELECT
        scope,
        embedding_type,
        dim,
        model,
        COUNT(*) AS count,
        -- Use approx_count_distinct for doc_id (high cardinality) - exact count unnecessary for diagnostics
        approx_count_distinct(doc_id) AS unique_docs,
        MIN(updated_at) AS oldest,
        MAX(updated_at) AS newest
    FROM document_embedding
    GROUP BY scope, embedding_type, dim, model
    ORDER BY scope, embedding_type, dim
);
