-- Internal candidate generator for read => find.
-- Scores precomputed document chunks inside an explicit URI scope, then returns
-- top chunks globally with per-document caps. This avoids large glob fanout.

CREATE OR REPLACE MACRO _find_candidates(
    q,
    uri_json,
    max_chunks := 256,
    per_doc_limit := 3,
    min_sem := NULL
) AS TABLE (
WITH
params AS (
    SELECT
        COALESCE(TRIM(q), '') AS raw_query,
        COALESCE(uri_json, '[]') AS uri_json_raw,
        CAST(GREATEST(1, COALESCE(max_chunks, 256)) AS BIGINT) AS chunk_cap,
        CAST(GREATEST(1, COALESCE(per_doc_limit, 3)) AS BIGINT) AS per_doc_cap,
        CAST(min_sem AS DOUBLE) AS min_sem_score
),
scope_uris AS (
    SELECT DISTINCT json_extract_string(j.value, '$') AS uri
    FROM params p,
         json_each(p.uri_json_raw) j
    WHERE json_extract_string(j.value, '$') IS NOT NULL
),
-- Cast to FLOAT[] in CTE definition to prevent DuckDB re-evaluation at use sites.
query_vec AS (
    SELECT embed_query(p.raw_query)::FLOAT[] AS vec
    FROM params p
    WHERE p.raw_query <> ''
),
full_scores AS (
    SELECT
        de.uri, de.node_id, de.doc_id, de.chunk_index, de.start_byte, de.end_byte,
        'full' AS embedding_type,
        calibrated_cosine(qv.vec, de.embedding) AS sem_score
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN scope_uris su ON su.uri = de.uri
    WHERE de.scope = 'document'
      AND de.embedding_type = 'full'
      AND de.dim = array_length(qv.vec)
),
full_docs AS (
    SELECT DISTINCT uri
    FROM full_scores
),
structure_scores AS (
    SELECT
        de.uri, de.node_id, de.doc_id, de.chunk_index, de.start_byte, de.end_byte,
        'structure' AS embedding_type,
        calibrated_cosine(qv.vec, de.embedding) AS sem_score
    FROM query_vec qv
    JOIN document_embedding de ON de.embedding IS NOT NULL
    JOIN scope_uris su ON su.uri = de.uri
    LEFT JOIN full_docs fd ON fd.uri = de.uri
    WHERE de.scope = 'document'
      AND de.embedding_type = 'structure'
      AND de.dim = array_length(qv.vec)
      AND fd.uri IS NULL
),
candidate_scores AS (
    SELECT * FROM full_scores
    UNION ALL
    SELECT * FROM structure_scores
),
per_doc_ranked AS (
    SELECT
        cs.*,
        ROW_NUMBER() OVER (
            PARTITION BY cs.uri
            ORDER BY cs.sem_score DESC, cs.chunk_index
        ) AS per_doc_rank
    FROM candidate_scores cs
),
filtered AS (
    SELECT pdr.*
    FROM per_doc_ranked pdr
    JOIN params p ON TRUE
    WHERE pdr.per_doc_rank <= p.per_doc_cap
      AND (p.min_sem_score IS NULL OR pdr.sem_score >= p.min_sem_score)
),
global_ranked AS (
    SELECT
        f.*,
        ROW_NUMBER() OVER (
            ORDER BY f.sem_score DESC, f.uri, f.chunk_index
        ) AS global_rank
    FROM filtered f
)
SELECT
    uri,
    node_id,
    doc_id,
    chunk_index,
    start_byte,
    end_byte,
    embedding_type,
    sem_score,
    per_doc_rank,
    global_rank
FROM global_ranked
WHERE global_rank <= (SELECT chunk_cap FROM params)
ORDER BY sem_score DESC, uri, chunk_index
);
