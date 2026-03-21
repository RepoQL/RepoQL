-- Search debugging and diagnostic macros.
-- Use these to understand semantic search behavior and benchmark the linear path.

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
    embedding_counts AS (
        SELECT
            (SELECT COUNT(*) FROM document_embedding WHERE scope = 'document' AND embedding_type = 'structure') AS structure_embeddings,
            (SELECT COUNT(*) FROM document_embedding WHERE scope = 'document' AND embedding_type = 'full') AS full_embeddings
    )
    SELECT
        di.query_dim,
        ec.structure_embeddings,
        ec.full_embeddings,
        'LINEAR_SCAN' AS search_path
    FROM dim_info di, embedding_counts ec
);

-- Test linear scan search directly (bypasses search macro).
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
        safe_cosine(qv.vec, de.embedding) AS score
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    WHERE de.scope = 'document'
      AND de.embedding_type = 'structure'
      AND de.dim = array_length(qv.vec)
    ORDER BY score DESC
    LIMIT k
);

CREATE OR REPLACE MACRO _embedding_stats() AS TABLE (
    SELECT
        scope,
        embedding_type,
        dim,
        model,
        COUNT(*) AS count,
        approx_count_distinct(doc_id) AS unique_docs,
        MIN(updated_at) AS oldest,
        MAX(updated_at) AS newest
    FROM document_embedding
    GROUP BY scope, embedding_type, dim, model
    ORDER BY scope, embedding_type, dim
);
