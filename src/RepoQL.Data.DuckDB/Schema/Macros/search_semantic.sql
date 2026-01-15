-- Semantic search module: embedding-based similarity with HNSW acceleration.
-- Uses VSS HNSW indexes when available (384-dim), falls back to linear scan otherwise.
-- Combines structure embeddings (fast, always available) with full-text embeddings (detailed).

CREATE OR REPLACE MACRO _search_semantic(
    q,
    uri_glob := NULL,
    mime_glob := NULL,
    max_cand := 5000
) AS TABLE (
WITH
-- Normalize parameters
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        CASE WHEN COALESCE(TRIM(q), '') = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        NULLIF(TRIM(uri_glob), '') AS uri_filter,
        NULLIF(TRIM(mime_glob), '') AS mime_filter,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand
),

-- Pre-filter repo_index with URI/MIME filters
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
    JOIN params p ON TRUE
    WHERE (
            p.uri_filter IS NULL
            OR repoql_glob_match(fs.uri, p.uri_filter, 'true','file:///') IS TRUE
            OR repoql_glob_match(fs.uri_local, p.uri_filter, 'true',NULL) IS TRUE
        )
      AND (
            p.mime_filter IS NULL
            OR repoql_glob_match(COALESCE(fs.mime, ''), p.mime_filter, 'true',NULL) IS TRUE
        )
),

-- Generate query embedding (only if query is non-empty)
query_vec AS (
    SELECT embed_text('Represent this sentence for searching relevant passages: ' || p.raw_query) AS vec
    FROM params p
    WHERE p.keywords_empty = FALSE
),

-- Check VSS index availability
vss_ready AS (
    SELECT
        (SELECT COUNT(*) FROM _vss_index_384 LIMIT 1) > 0 AS has_384,
        (SELECT COUNT(*) FROM _vss_index_768 LIMIT 1) > 0 AS has_768,
        (SELECT COUNT(*) FROM _vss_index_1024 LIMIT 1) > 0 AS has_1024,
        (SELECT array_length(vec::FLOAT[]) FROM query_vec WHERE vec IS NOT NULL) AS query_dim
),

-- ============================================================================
-- HNSW FAST PATH: Use VSS index for 384-dim embeddings
-- ============================================================================
hnsw_structure AS (
    SELECT
        v.doc_id,
        v.node_id,
        1.0 - array_cosine_distance(v.vec, qv.vec::FLOAT[384]) AS struct_sem,
        'hnsw' AS source
    FROM query_vec qv, _vss_index_384 v
    WHERE qv.vec IS NOT NULL
      AND v.embedding_type = 'structure'
      AND (SELECT query_dim FROM vss_ready) = 384
      AND (SELECT has_384 FROM vss_ready) = TRUE
    ORDER BY array_cosine_distance(v.vec, qv.vec::FLOAT[384])
    LIMIT 500
),

-- ============================================================================
-- LINEAR FALLBACK: Use when HNSW not available or different dimensions
-- ============================================================================
linear_structure AS (
    SELECT
        de.doc_id,
        de.node_id,
        list_cosine_similarity(qv.vec::FLOAT[], de.embedding) AS struct_sem,
        'linear' AS source
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN filtered ri ON ri.node_id = de.node_id
    WHERE qv.vec IS NOT NULL
      AND de.scope = 'document'
      AND de.embedding_type = 'structure'
      AND de.dim = array_length(qv.vec::FLOAT[])
      -- Only use linear if HNSW not available for this dimension
      AND NOT (
          (SELECT query_dim FROM vss_ready) = 384
          AND (SELECT has_384 FROM vss_ready) = TRUE
      )
),

-- Combine HNSW and linear results (only one will have data)
structure_sem AS (
    SELECT doc_id, node_id, struct_sem, source FROM hnsw_structure
    UNION ALL
    SELECT doc_id, node_id, struct_sem, source FROM linear_structure
),

-- ============================================================================
-- FULL-TEXT EMBEDDINGS: Always linear (more detailed, for chunk-level matching)
-- ============================================================================
full_text_chunks AS (
    SELECT
        de.doc_id,
        de.node_id,
        de.chunk_index,
        de.start_byte,
        de.end_byte,
        list_cosine_similarity(qv.vec::FLOAT[], de.embedding) AS chunk_sem
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN filtered ri ON ri.node_id = de.node_id
    WHERE qv.vec IS NOT NULL
      AND de.scope = 'document'
      AND de.embedding_type = 'full'
      AND de.dim = array_length(qv.vec::FLOAT[])
),

-- Aggregate full-text to best chunk per document
full_text_scored AS (
    SELECT
        node_id,
        doc_id,
        MAX(chunk_sem) AS full_sem,
        (ARRAY_AGG(chunk_index ORDER BY chunk_sem DESC))[1] AS best_chunk_index,
        (ARRAY_AGG(start_byte ORDER BY chunk_sem DESC))[1] AS best_chunk_start,
        (ARRAY_AGG(end_byte ORDER BY chunk_sem DESC))[1] AS best_chunk_end
    FROM full_text_chunks
    GROUP BY node_id, doc_id
),

-- ============================================================================
-- COMBINE: Merge structure + full-text with agreement boost
-- ============================================================================
combined AS (
    SELECT
        COALESCE(ss.node_id, fs.node_id) AS node_id,
        COALESCE(ss.doc_id, fs.doc_id) AS doc_id,
        ss.struct_sem,
        fs.full_sem,
        -- Use whichever scored higher, plus 5% boost when both agree
        GREATEST(COALESCE(ss.struct_sem, 0), COALESCE(fs.full_sem, 0))
            + CASE
                WHEN ss.struct_sem IS NOT NULL AND fs.full_sem IS NOT NULL
                THEN 0.05 * (ss.struct_sem + fs.full_sem)
                ELSE 0
            END AS sem_score,
        COALESCE(ss.source, 'full_only') AS search_source,
        fs.best_chunk_index,
        fs.best_chunk_start,
        fs.best_chunk_end
    FROM structure_sem ss
    FULL OUTER JOIN full_text_scored fs ON ss.node_id = fs.node_id
),

-- Rank and normalize
ranked AS (
    SELECT
        c.*,
        ROW_NUMBER() OVER (ORDER BY c.sem_score DESC, c.node_id) AS sem_rank
    FROM combined c
),

limited AS (
    SELECT r.*
    FROM ranked r
    JOIN params p ON TRUE
    WHERE r.sem_rank <= p.limit_cand
),

normalized AS (
    SELECT
        l.*,
        sem_cubed_boost(l.sem_score) AS sem_norm,
        rrf_score(l.sem_rank) AS rrf_sem
    FROM limited l
)

SELECT
    node_id,
    doc_id,
    sem_score,
    sem_norm,
    sem_rank,
    rrf_sem,
    search_source,
    struct_sem AS structure_score,
    full_sem AS fulltext_score,
    best_chunk_index,
    best_chunk_start,
    best_chunk_end
FROM normalized
);
