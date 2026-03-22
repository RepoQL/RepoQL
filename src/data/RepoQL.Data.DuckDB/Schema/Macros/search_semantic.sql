-- Semantic search module: exact embedding-based similarity using linear scans.
-- Combines structure embeddings (fast, always available) with full-text embeddings (detailed).
--
-- Performance notes (DuckDB TABLE macro pitfalls):
-- 1. Cast in CTE, not at use site. `qv.vec::FLOAT[]` at each use site causes DuckDB
--    to re-evaluate the CTE. `embed_query(...)::FLOAT[] AS vec` in the CTE definition
--    evaluates once. Difference: 4s → 1s.
-- 2. Resolve macro parameters into CTEs before use in QUALIFY/WHERE. Raw macro
--    parameter in QUALIFY triggers full pipeline re-evaluation. A CTE subquery
--    resolves to a constant the optimizer can fold. Difference: 18s → 1s.
-- 3. Single-pass GROUP BY replaces the FULL OUTER JOIN for combining embedding types.
-- 4. query_dim carried through pipeline to avoid re-referencing query_vec in calibration.

CREATE OR REPLACE MACRO _search_semantic(
    q,
    uri_glob := NULL,
    max_cand := 5000,
    uri_like := NULL
) AS TABLE (
WITH
-- Resolve macro parameters into CTE values. Critical for QUALIFY performance.
params AS (
    SELECT CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand
),

-- Cast to FLOAT[] in the CTE, not at use sites. DuckDB re-evaluates
-- the CTE when a cast expression appears at the use site (~4s → ~1s).
query_vec AS (
    SELECT embed_query(COALESCE(TRIM(q), ''))::FLOAT[] AS vec
    WHERE COALESCE(TRIM(q), '') <> ''
),

-- Single pass: score ALL document embeddings (structure + full) in one scan.
all_scored AS (
    SELECT
        de.doc_id,
        de.node_id,
        de.embedding_type,
        de.chunk_index,
        de.start_byte,
        de.end_byte,
        safe_cosine(qv.vec, de.embedding) AS score,
        array_length(qv.vec) AS query_dim
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN _scope_filter(uri_glob := uri_glob, uri_like := uri_like) sf
        ON sf.node_id = de.node_id
    WHERE qv.vec IS NOT NULL
      AND de.scope = 'document'
      AND de.dim = array_length(qv.vec)
),

-- Aggregate both embedding types per node — no FULL OUTER JOIN.
per_node AS (
    SELECT
        node_id,
        doc_id,
        MAX(CASE WHEN embedding_type = 'structure' THEN score END) AS struct_sem,
        MAX(CASE WHEN embedding_type = 'full' THEN score END) AS full_sem,
        ARG_MAX(
            CASE WHEN embedding_type = 'full' THEN chunk_index END,
            CASE WHEN embedding_type = 'full' THEN score ELSE -1 END
        ) AS best_chunk_index,
        ARG_MAX(
            CASE WHEN embedding_type = 'full' THEN start_byte END,
            CASE WHEN embedding_type = 'full' THEN score ELSE -1 END
        ) AS best_chunk_start,
        ARG_MAX(
            CASE WHEN embedding_type = 'full' THEN end_byte END,
            CASE WHEN embedding_type = 'full' THEN score ELSE -1 END
        ) AS best_chunk_end,
        MAX(query_dim) AS query_dim
    FROM all_scored
    GROUP BY node_id, doc_id
),

combined AS (
    SELECT
        node_id,
        doc_id,
        struct_sem,
        full_sem,
        GREATEST(COALESCE(struct_sem, 0), COALESCE(full_sem, 0))
            + CASE
                WHEN struct_sem IS NOT NULL AND full_sem IS NOT NULL
                THEN 0.05 * (struct_sem + full_sem)
                ELSE 0
            END AS sem_score,
        CASE WHEN struct_sem IS NOT NULL THEN 'linear' ELSE 'full_only' END AS search_source,
        best_chunk_index,
        best_chunk_start,
        best_chunk_end,
        query_dim
    FROM per_node
),

-- QUALIFY uses CTE subquery, NOT raw macro parameter. See performance notes.
ranked AS (
    SELECT
        c.*,
        ROW_NUMBER() OVER (ORDER BY c.sem_score DESC, c.node_id) AS sem_rank
    FROM combined c
    QUALIFY sem_rank <= (SELECT limit_cand FROM params)
),

-- Calibration + normalization using window functions (no separate sem_stats CTE).
-- query_dim carried from all_scored to avoid re-referencing query_vec.
normalized AS (
    SELECT
        node_id,
        doc_id,
        sem_score,
        sem_calibrate(sem_score, query_dim)
            * sem_query_confidence(
                MAX(sem_score) OVER (),
                quantile_cont(sem_score, 0.90) OVER (),
                COUNT(*) OVER (),
                query_dim
            ) AS sem_norm,
        sem_rank,
        rrf_score(sem_rank) AS rrf_sem,
        search_source,
        struct_sem AS structure_score,
        full_sem AS fulltext_score,
        best_chunk_index,
        best_chunk_start,
        best_chunk_end
    FROM ranked
)

SELECT * FROM normalized
);
