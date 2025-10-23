CREATE OR REPLACE MACRO snippet(u, context_lines) AS TABLE (
WITH base AS (
    SELECT
        repository_uri_container(u)     AS base,
        repository_uri_fragment(u)      AS frag,
        repository_uri_fragment_kind(u) AS kind,
        repository_uri_line_start(u)    AS l1,
        repository_uri_line_end(u)      AS l2
),
     doc AS (
         SELECT n.id AS doc_id, n.uri AS uri, a.text_content, a.media_type, a.storage_uri
         FROM base b
                  JOIN node n ON n.container_uri_lowercase = lower(b.base)
                  LEFT JOIN artifact a ON a.id = n.artifact_id
     ),
     edge_focus AS (
         SELECT e.id AS edge_id,
                ss.start_line   AS el1, ss.end_line   AS el2,
                ss.start_column AS ec1, ss.end_column AS ec2
         FROM base b
                  JOIN edge e ON b.frag LIKE 'edge=%' AND substr(b.frag, 6) = CAST(e.id AS VARCHAR)
                  LEFT JOIN span ss ON ss.id = e.source_span_id
     ),
     char_rng AS (
         SELECT
             CASE WHEN kind='char' THEN try_cast(split_part(substr(frag, 6), ',', 1) AS BIGINT) END AS c1,
             CASE WHEN kind='char' THEN try_cast(NULLIF(split_part(substr(frag, 6), ',', 2), '') AS BIGINT) END AS c2
         FROM base
     ),
     focus AS (
         SELECT
             COALESCE(
                 (SELECT el1 FROM edge_focus),
                 (SELECT l1  FROM base),
                 (SELECT line_for_byte_offset(text_content, c1) FROM doc, char_rng),
                 1
             ) AS fl1,
             COALESCE(
                 (SELECT el2 FROM edge_focus),
                 (SELECT l2  FROM base),
                 (SELECT NULLIF(line_for_byte_offset(text_content, c2), 0) FROM doc, char_rng)
             ) AS fl2,
             COALESCE(
                 (SELECT ec1 FROM edge_focus),
                 (SELECT column_for_byte_offset(text_content, c1) FROM doc, char_rng)
             ) AS fc1,
             COALESCE(
                 (SELECT ec2 FROM edge_focus),
                 (SELECT column_for_byte_offset(text_content, c2) FROM doc, char_rng)
             ) AS fc2
     ),
     raw_text AS (
         SELECT
             CASE WHEN text_content IS NOT NULL THEN text_content
                  ELSE COALESCE(binary_preview(storage_uri, 4096), '')
                 END AS content
         FROM doc
     ),
     lines AS (
         SELECT
                 ROW_NUMBER() OVER () AS ln,
                 value AS line
         FROM raw_text,
              UNNEST(string_split(content, CHR(10))) AS t(value)
     ),
     win AS (
         SELECT
             GREATEST(1, COALESCE(fl1,1) - COALESCE(context_lines,3)) AS w1,
             COALESCE(COALESCE(fl2,fl1) + COALESCE(context_lines,3), 1 + COALESCE(context_lines,3)*2) AS w2
         FROM focus
     )
SELECT
    ln AS line_number,
    line AS text,
    (ln BETWEEN fl1 AND COALESCE(fl2, fl1)) AS is_focus,
    CASE WHEN ln BETWEEN fl1 AND COALESCE(fl2, fl1) THEN fc1 ELSE NULL END AS focus_start_column,
    CASE WHEN ln BETWEEN fl1 AND COALESCE(fl2, fl1) THEN fc2 ELSE NULL END AS focus_end_column,
    language_from_media_type_or_uri((SELECT media_type FROM doc), (SELECT uri FROM doc)) AS language,
    (SELECT uri FROM doc) AS document_uri,
    repository_uri_join(
        (SELECT uri FROM doc),
        'line=' || CAST(fl1 AS VARCHAR) || COALESCE(',' || CAST(fl2 AS VARCHAR), '')
    ) AS resolved_uri
FROM lines, win, focus
WHERE ln BETWEEN w1 AND w2
ORDER BY ln
    );