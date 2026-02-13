-- Finds files semantically similar to a seed URI (document or fragment).
-- Similarity is computed from full-text embedding chunks and reduced to best match per file.
--
-- Parameters:
--   seed_uri    - Seed URI (supports bare file URI, #symbol=..., or #line=start,end)
--   scope_glob  - Optional URI glob scope (resolved via glob_files)
--   k           - Maximum number of similar files to return (default 20)
--
-- Returns:
--   uri         - Matched file URI
--   similarity  - Cosine similarity score
--   headline    - Artifact headline

CREATE OR REPLACE MACRO find_similar(
    seed_uri,
    scope_glob := NULL,
    k := 20
) AS TABLE (
WITH
params AS (
    SELECT
        COALESCE(TRIM(seed_uri), '') AS seed_uri,
        NULLIF(TRIM(scope_glob), '') AS scope_glob,
        CAST(COALESCE(k, 20) AS BIGINT) AS result_k
),

seed_parts AS (
    SELECT
        repository_uri_container(p.seed_uri) AS base,
        repository_uri_fragment_kind(p.seed_uri) AS fragment_kind,
        repository_uri_symbol(p.seed_uri) AS symbol_name,
        regexp_extract(COALESCE(repository_uri_symbol(p.seed_uri), ''), '[^.]+$', 0) AS symbol_tail,
        TRY_CAST(repository_uri_line_start(p.seed_uri) AS BIGINT) AS line_start,
        TRY_CAST(repository_uri_line_end(p.seed_uri) AS BIGINT) AS line_end
    FROM params p
),

seed_range_candidates AS (
    -- Case 1: no fragment -> range over all non-document objects in file
    SELECT
        MIN(s.start_byte) AS start_byte,
        MAX(s.end_byte) AS end_byte,
        1 AS priority
    FROM seed_parts sp
    JOIN node doc ON doc.uri = sp.base AND doc.kind = 'document'
    JOIN span s ON s.document_id = doc.id
    JOIN node child ON child.span_id = s.id
    WHERE sp.fragment_kind IS NULL
      AND child.kind <> 'document'

    UNION ALL

    -- Case 2: symbol fragment -> resolve by name tail, choose largest matching span
    SELECT start_byte, end_byte, priority FROM (
        SELECT
            s.start_byte AS start_byte,
            s.end_byte AS end_byte,
            2 AS priority
        FROM seed_parts sp
        JOIN node doc ON doc.uri = sp.base AND doc.kind = 'document'
        JOIN node child ON child.span_id IS NOT NULL
        JOIN span s ON s.id = child.span_id AND s.document_id = doc.id
        WHERE sp.fragment_kind = 'symbol'
          AND sp.symbol_tail <> ''
          AND lower(COALESCE(json_extract_string(child.properties, '$.name'), '')) = lower(sp.symbol_tail)
        ORDER BY (s.end_byte - s.start_byte) DESC
        LIMIT 1
    )

    UNION ALL

    -- Case 3: line fragment -> convert 1-based line range into byte offsets
    SELECT
        CAST(
            COALESCE(
                list_sum(
                    list_transform(
                        lines[:GREATEST(0, CAST(GREATEST(1, COALESCE(sp.line_start, 1)) - 1 AS INTEGER))],
                        x -> length(x) + 1
                    )
                ),
                0
            )
            AS BIGINT
        ) AS start_byte,
        CAST(
            COALESCE(
                list_sum(
                    list_transform(
                        lines[:GREATEST(1, CAST(COALESCE(sp.line_end, sp.line_start, 1) AS INTEGER))],
                        x -> length(x) + 1
                    )
                ),
                0
            )
            AS BIGINT
        ) AS end_byte,
        3 AS priority
    FROM seed_parts sp
    JOIN node doc ON doc.uri = sp.base AND doc.kind = 'document'
    JOIN artifact a ON a.id = doc.artifact_id
    CROSS JOIN LATERAL (SELECT string_split(COALESCE(a.text_content, ''), chr(10)) AS lines)
    WHERE sp.fragment_kind = 'line'
),

seed_range AS (
    SELECT start_byte, end_byte
    FROM seed_range_candidates
    QUALIFY ROW_NUMBER() OVER (ORDER BY priority) = 1

    UNION ALL

    SELECT NULL AS start_byte, NULL AS end_byte
    WHERE NOT EXISTS (SELECT 1 FROM seed_range_candidates)
),

seed_chunks AS (
    SELECT
        CASE
            WHEN sr.start_byte IS NULL OR de.start_byte IS NULL THEN de.embedding
            WHEN de.start_byte >= sr.start_byte AND de.end_byte <= sr.end_byte THEN de.embedding
            ELSE embed_passage(
                substr(
                    a.text_content,
                    GREATEST(de.start_byte, sr.start_byte) + 1,
                    LEAST(de.end_byte, sr.end_byte) - GREATEST(de.start_byte, sr.start_byte)
                )
            )::FLOAT[]
        END AS embedding
    FROM seed_parts sp
    CROSS JOIN seed_range sr
    JOIN node n ON n.uri = sp.base AND n.kind = 'document'
    JOIN artifact a ON a.id = n.artifact_id
    JOIN document_embedding de ON de.uri = sp.base
    WHERE de.embedding_type = 'full'
      AND (
            sr.start_byte IS NULL
            OR NOT (de.end_byte < sr.start_byte OR de.start_byte > sr.end_byte)
      )
),

scope_filter AS (
    SELECT DISTINCT gf.uri
    FROM params p
    CROSS JOIN glob_files(p.scope_glob) gf
    WHERE p.scope_glob IS NOT NULL
),

chunk_pairs AS (
    SELECT
        de.uri,
        de.node_id,
        list_cosine_similarity(sc.embedding, de.embedding) AS similarity
    FROM document_embedding de
    CROSS JOIN seed_chunks sc
    CROSS JOIN seed_parts sp
    CROSS JOIN params p
    LEFT JOIN scope_filter sf ON sf.uri = de.uri
    WHERE de.uri <> sp.base
      AND de.embedding_type = 'full'
      AND (p.scope_glob IS NULL OR sf.uri IS NOT NULL)
),

best_per_doc AS (
    SELECT
        uri,
        node_id,
        similarity,
        ROW_NUMBER() OVER (PARTITION BY uri ORDER BY similarity DESC NULLS LAST) AS rn
    FROM chunk_pairs
    QUALIFY rn = 1
),

repo_headlines AS (
    SELECT
        ri.uri,
        ri.headline,
        ROW_NUMBER() OVER (
            PARTITION BY ri.uri
            ORDER BY CASE WHEN ri.scope = 'document' THEN 0 ELSE 1 END, ri.node_id
        ) AS rn
    FROM repo_index ri
)

SELECT
    bpd.uri,
    bpd.similarity,
    rh.headline
FROM best_per_doc bpd
JOIN repo_headlines rh ON rh.uri = bpd.uri AND rh.rn = 1
JOIN params p ON TRUE
WHERE p.scope_glob IS NULL OR EXISTS (
    SELECT 1
    FROM scope_filter sf
    WHERE sf.uri = bpd.uri
)
ORDER BY bpd.similarity DESC NULLS LAST, LENGTH(bpd.uri), bpd.uri
LIMIT (SELECT result_k FROM params)
);
