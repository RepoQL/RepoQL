-- Semantic search module: exact embedding-based similarity using linear scans.
-- Combines structure embeddings (fast, always available) with full-text embeddings (detailed).

CREATE OR REPLACE MACRO _search_semantic(
    q,
    uri_glob := NULL,
    max_cand := 5000,
    uri_like := NULL
) AS TABLE (
WITH
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        CASE WHEN COALESCE(TRIM(q), '') = '' THEN TRUE ELSE FALSE END AS keywords_empty,
        CAST(COALESCE(max_cand, 5000) AS BIGINT) AS limit_cand
),

_sem_scope AS (
    SELECT * FROM _scope_filter(
        uri_glob := uri_glob,
        uri_like := uri_like
    )
),

query_vec AS (
    SELECT embed_query(p.raw_query) AS vec
    FROM params p
    WHERE p.keywords_empty = FALSE
),

query_info AS (
    SELECT array_length(vec::FLOAT[]) AS query_dim
    FROM query_vec
    WHERE vec IS NOT NULL
),

structure_sem AS (
    SELECT
        de.doc_id,
        de.node_id,
        list_cosine_similarity(qv.vec::FLOAT[], de.embedding) AS struct_sem,
        'linear' AS source
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN _sem_scope sf ON sf.node_id = de.node_id
    WHERE qv.vec IS NOT NULL
      AND de.scope = 'document'
      AND de.embedding_type = 'structure'
      AND de.dim = array_length(qv.vec::FLOAT[])
),

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
    JOIN _sem_scope sf ON sf.node_id = de.node_id
    WHERE qv.vec IS NOT NULL
      AND de.scope = 'document'
      AND de.embedding_type = 'full'
      AND de.dim = array_length(qv.vec::FLOAT[])
),

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

combined AS (
    SELECT
        COALESCE(ss.node_id, fs.node_id) AS node_id,
        COALESCE(ss.doc_id, fs.doc_id) AS doc_id,
        ss.struct_sem,
        fs.full_sem,
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

limited AS (
    SELECT
        c.*,
        ROW_NUMBER() OVER (ORDER BY c.sem_score DESC, c.node_id) AS sem_rank
    FROM combined c
    QUALIFY sem_rank <= (SELECT limit_cand FROM params)
),

sem_stats AS (
    SELECT
        COUNT(*) AS candidate_count,
        MAX(sem_score) AS top_sem,
        quantile_cont(sem_score, 0.90) AS p90_sem
    FROM limited
),

calibrated AS (
    SELECT
        l.*,
        sem_calibrate(l.sem_score, (SELECT query_dim FROM query_info)) AS sem_base_norm,
        sem_query_confidence(
            (SELECT top_sem FROM sem_stats),
            (SELECT p90_sem FROM sem_stats),
            (SELECT candidate_count FROM sem_stats),
            (SELECT query_dim FROM query_info)
        ) AS sem_query_conf
    FROM limited l
),

normalized AS (
    SELECT
        c.*,
        c.sem_base_norm * c.sem_query_conf AS sem_norm,
        rrf_score(c.sem_rank) AS rrf_sem
    FROM calibrated c
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
