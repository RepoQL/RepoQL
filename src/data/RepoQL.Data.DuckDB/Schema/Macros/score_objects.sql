-- Object scoring for the second pass of search.
-- Maps lexical grep hits and semantic chunk scores onto child objects within
-- a bounded set of already-ranked documents.

CREATE OR REPLACE MACRO _score_objects(
    keywords,
    doc_ids_json,
    grep_json,
    query_vec,
    query_dim,
    per_doc_cap := 20
) AS TABLE (
WITH
params AS (
    SELECT
        LOWER(COALESCE(TRIM(keywords), '')) AS keywords_lc,
        COALESCE(doc_ids_json, '[]') AS doc_ids_json_raw,
        COALESCE(grep_json, '[]') AS grep_json_raw,
        CAST(query_vec AS FLOAT[]) AS query_vec_f32,
        CAST(query_dim AS INTEGER) AS query_dim_value,
        CAST(GREATEST(1, COALESCE(per_doc_cap, 20)) AS BIGINT) AS per_doc_limit
),

doc_ids AS (
    SELECT DISTINCT TRY_CAST(json_extract_string(j.value, '$') AS UUID) AS doc_id
    FROM params p,
         json_each(p.doc_ids_json_raw::JSON) j
    WHERE TRY_CAST(json_extract_string(j.value, '$') AS UUID) IS NOT NULL
),

grep_data AS (
    SELECT DISTINCT
        TRY_CAST(json_extract_string(j.value, '$.doc_id') AS UUID) AS doc_id,
        TRY_CAST(json_extract_string(j.value, '$.line_number') AS INTEGER) AS line_number
    FROM params p,
         json_each(p.grep_json_raw::JSON) j
    WHERE TRY_CAST(json_extract_string(j.value, '$.doc_id') AS UUID) IS NOT NULL
      AND TRY_CAST(json_extract_string(j.value, '$.line_number') AS INTEGER) IS NOT NULL
),

children AS (
    SELECT
        child.id AS node_id,
        s.document_id AS doc_id,
        s.start_line,
        s.end_line,
        s.start_byte,
        s.end_byte,
        LOWER(COALESCE(
            repository_uri_symbol(child.uri),
            json_extract_string(child.properties, '$.symbol'),
            json_extract_string(child.properties, '$.name'),
            ''
        )) AS symbol_key,
        LOWER(COALESCE(child.headline, '') || ' ' || COALESCE(child.structure, '')) AS headline_text
    FROM doc_ids d
    JOIN span s ON s.document_id = d.doc_id
    JOIN node child ON child.span_id = s.id
    WHERE child.kind <> 'document'
),

scored_objects AS (
    SELECT
        c.node_id,
        c.doc_id,
        c.start_line,
        CASE
            WHEN p.keywords_lc <> '' AND c.symbol_key = p.keywords_lc THEN 4.0
            WHEN p.keywords_lc <> '' AND position(p.keywords_lc IN c.symbol_key) > 0 THEN 3.2
            ELSE 0.0
        END AS symbol_score,
        COUNT(DISTINCT g.line_number) AS grep_hits,
        CASE
            WHEN COUNT(DISTINCT g.line_number) >= 2 THEN 2.5 + 0.1 * COUNT(DISTINCT g.line_number)
            WHEN COUNT(DISTINCT g.line_number) = 1 THEN 2.0
            ELSE 0.0
        END AS grep_score,
        CASE
            WHEN p.keywords_lc <> '' AND position(p.keywords_lc IN c.headline_text) > 0 THEN TRUE
            ELSE FALSE
        END AS headline_hit,
        CASE
            WHEN p.keywords_lc <> '' AND position(p.keywords_lc IN c.headline_text) > 0 THEN 1.5
            ELSE 0.0
        END AS headline_score,
        MAX(
            CASE
                WHEN p.query_vec_f32 IS NOT NULL AND p.query_dim_value IS NOT NULL
                THEN calibrated_cosine(p.query_vec_f32, de.embedding)
                ELSE NULL
            END
        ) AS chunk_sem
    FROM children c
    CROSS JOIN params p
    LEFT JOIN grep_data g
        ON g.doc_id = c.doc_id
       AND c.start_line IS NOT NULL
       AND c.end_line IS NOT NULL
       AND g.line_number BETWEEN c.start_line AND c.end_line
    LEFT JOIN document_embedding de
        ON p.query_vec_f32 IS NOT NULL
       AND p.query_dim_value IS NOT NULL
       AND de.doc_id = c.doc_id
       AND de.scope = 'document'
       AND de.embedding_type = 'full'
       AND de.embedding IS NOT NULL
       AND de.dim = p.query_dim_value
       AND c.start_byte IS NOT NULL
       AND c.end_byte IS NOT NULL
       AND de.start_byte IS NOT NULL
       AND de.end_byte IS NOT NULL
       AND de.start_byte < c.end_byte
       AND de.end_byte > c.start_byte
    GROUP BY
        c.node_id,
        c.doc_id,
        c.start_line,
        c.symbol_key,
        c.headline_text,
        p.keywords_lc
),

object_scores AS (
    SELECT
        node_id,
        doc_id,
        GREATEST(symbol_score, grep_score, headline_score) + 0.3 * COALESCE(chunk_sem, 0) AS object_score,
        symbol_score,
        grep_hits,
        chunk_sem,
        headline_hit,
        start_line
    FROM scored_objects
),

ranked AS (
    SELECT
        os.*,
        ROW_NUMBER() OVER (
            PARTITION BY os.doc_id
            ORDER BY os.object_score DESC, os.grep_hits DESC, os.start_line NULLS LAST, os.node_id
        ) AS object_rank
    FROM object_scores os
)

SELECT
    node_id,
    doc_id,
    object_score,
    symbol_score,
    grep_hits,
    chunk_sem,
    headline_hit
FROM ranked
WHERE object_score > 0
  AND object_rank <= (SELECT per_doc_limit FROM params)
ORDER BY doc_id, object_score DESC, grep_hits DESC, node_id
);
