-- Semantic search module: embedding-based similarity with HNSW acceleration.
-- Uses VSS HNSW indexes when available (384-dim), falls back to linear scan otherwise.
-- Combines structure embeddings (fast, always available) with full-text embeddings (detailed).

CREATE OR REPLACE MACRO _search_semantic(
    q,
    uri_glob := NULL,
    mime_glob := NULL,
    max_cand := 5000,
    uri_like := NULL
) AS TABLE (
WITH
-- Normalize parameters
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        CASE WHEN COALESCE(TRIM(q), '') = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        NULLIF(TRIM(uri_glob), '') AS uri_filter,
        NULLIF(TRIM(uri_like), '') AS uri_like_filter,
        NULLIF(TRIM(mime_glob), '') AS mime_filter,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand
),

-- Pre-filter repo_index with URI/MIME filters
filtered_source AS (
    SELECT
        ri.*,
        split_part(ri.uri, '#', 1) AS uri_container,
        CASE
            WHEN ri.uri IS NULL THEN NULL
            ELSE regexp_replace(LOWER(ri.uri), '^[^:]+://+', '')
        END AS uri_local
    FROM repo_index ri
),
scope_uris AS (
    SELECT DISTINCT
        gf.uri AS scoped_uri,
        split_part(gf.uri, '#', 1) AS scoped_container_uri
    FROM params p
    CROSS JOIN glob_files(pattern_spec := p.uri_filter) gf
    WHERE p.uri_filter IS NOT NULL
),
filtered AS (
    SELECT fs.*
    FROM filtered_source fs
    JOIN params p ON TRUE
    WHERE (
            p.uri_filter IS NULL
            OR EXISTS (
                SELECT 1
                FROM scope_uris su
                WHERE su.scoped_uri = fs.uri
                   OR su.scoped_container_uri = fs.uri_container
            )
            OR matches_glob(fs.uri, p.uri_filter, TRUE, 'file:///') IS TRUE
            OR matches_glob(fs.uri_local, p.uri_filter, TRUE, NULL) IS TRUE
        )
      AND (
            p.uri_like_filter IS NULL
            OR fs.uri LIKE p.uri_like_filter
        )
      AND (
            p.mime_filter IS NULL
            OR repoql_glob_match(COALESCE(fs.mime, ''), p.mime_filter, 'true',NULL) IS TRUE
        )
),

-- Generate query embedding (only if query is non-empty)
-- Uses embed_query() which handles E5 model prefixes automatically
query_vec AS (
    SELECT embed_query(p.raw_query) AS vec
    FROM params p
    WHERE p.keywords_empty = FALSE
),

-- Check VSS index availability
vss_rebuild_state AS (
    SELECT
        COALESCE(
            (
                SELECT LOWER(TRIM(value)) = 'true'
                FROM metadata
                WHERE key = 'vss_structure_ready'
                LIMIT 1
            ),
            FALSE
        ) AS structure_ready
),

vss_ready AS (
    SELECT
        (SELECT COUNT(*) FROM _vss_index_384 LIMIT 1) > 0 AS has_384,
        (SELECT COUNT(*) FROM _vss_index_768 LIMIT 1) > 0 AS has_768,
        (SELECT COUNT(*) FROM _vss_index_1024 LIMIT 1) > 0 AS has_1024,
        (SELECT array_length(vec::FLOAT[]) FROM query_vec WHERE vec IS NOT NULL) AS query_dim,
        (SELECT structure_ready FROM vss_rebuild_state) AS structure_ready
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
    JOIN filtered ri ON ri.node_id = v.node_id
    WHERE qv.vec IS NOT NULL
      AND v.embedding_type = 'structure'
      AND (SELECT query_dim FROM vss_ready) = 384
      AND (SELECT has_384 FROM vss_ready) = TRUE
      AND (SELECT structure_ready FROM vss_ready) = TRUE
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
          AND (SELECT structure_ready FROM vss_ready) = TRUE
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

-- Rank chunks once and keep only the best-scoring row per document.
-- This avoids ARRAY_AGG materialization for large files/scope.
full_text_ranked AS (
    SELECT
        node_id,
        doc_id,
        chunk_index,
        start_byte,
        end_byte,
        chunk_sem,
        ROW_NUMBER() OVER (
            PARTITION BY node_id
            ORDER BY chunk_sem DESC, chunk_index
        ) AS chunk_rank
    FROM full_text_chunks
),
full_text_scored AS (
    SELECT
        node_id,
        doc_id,
        chunk_sem AS full_sem,
        chunk_index AS best_chunk_index,
        start_byte AS best_chunk_start,
        end_byte AS best_chunk_end
    FROM full_text_ranked
    WHERE chunk_rank = 1
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

-- Rank, filter, and normalize via QUALIFY for early termination
limited AS (
    SELECT
        c.*,
        ROW_NUMBER() OVER (ORDER BY c.sem_score DESC, c.node_id) AS sem_rank
    FROM combined c
    QUALIFY sem_rank <= (SELECT limit_cand FROM params)
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
