CREATE OR REPLACE MACRO snippet(u, context_lines) AS TABLE (
WITH base AS (
    SELECT
        repository_uri_container(u)     AS base,
        repository_uri_fragment(u)      AS frag,
        repository_uri_fragment_kind(u) AS kind,
        TRY_CAST(
            CASE
                WHEN repository_uri_fragment(u) LIKE 'edge=%'
                THEN substr(repository_uri_fragment(u), 6)
            END AS UUID
        ) AS edge_id,
        TRY_CAST(repository_uri_line_start(u) AS INTEGER)    AS l1,
        TRY_CAST(repository_uri_line_end(u) AS INTEGER)      AS l2,
        repository_uri_symbol(u)        AS symbol
),
     doc AS (
         SELECT n.id AS doc_id, n.uri AS uri, a.text_content, a.media_type, a.storage_uri
         FROM base b
                  JOIN node n ON (n.container_uri_lowercase = lower(b.base) OR lower(n.uri) = lower(b.base))
                      AND n.kind = 'document'
                  LEFT JOIN artifact a ON a.id = n.artifact_id
     ),
     edge_focus AS (
         SELECT e.id AS edge_id,
                ss.start_line   AS el1, ss.end_line   AS el2,
                ss.start_column AS ec1, ss.end_column AS ec2
         FROM base b
                  JOIN edge e ON b.edge_id IS NOT NULL AND e.id = b.edge_id
                  LEFT JOIN span ss ON ss.id = e.source_span_id
     ),
     symbol_focus AS (
         SELECT
             sym.node_id,
             sym.sl1,
             sym.sl2,
             sym.sc1,
             sym.sc2
         FROM base b
                  LEFT JOIN doc d ON TRUE
                  LEFT JOIN LATERAL (
             SELECT
                 n.id AS node_id,
                 s.start_line   AS sl1,
                 s.end_line     AS sl2,
                 s.start_column AS sc1,
                 s.end_column   AS sc2
             FROM node n
                      LEFT JOIN span s ON s.id = n.span_id
             WHERE d.doc_id IS NOT NULL
               AND s.document_id = d.doc_id
               AND b.symbol IS NOT NULL
               AND (
                       lower(coalesce(json_extract_string(n.properties, '$.qualified_name'), '')) = lower(b.symbol)
                    OR lower(coalesce(json_extract_string(n.properties, '$.name'), '')) = lower(b.symbol)
                    OR lower(coalesce(json_extract_string(n.properties, '$.slug'), '')) = lower(b.symbol)
                    OR lower(coalesce(json_extract_string(n.properties, '$.identifier'), '')) = lower(b.symbol)
                    OR lower(coalesce(node_display_label(n.kind, CAST(n.properties AS VARCHAR)), '')) = lower(b.symbol)
               )
             ORDER BY
                 s.start_line NULLS LAST,
                 s.start_column NULLS LAST,
                 n.updated_at DESC
             LIMIT 1
         ) sym ON true
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
                 (SELECT sl1 FROM symbol_focus),
                 (SELECT el1 FROM edge_focus),
                 (SELECT l1  FROM base),
                 (SELECT TRY_CAST(line_for_byte_offset(text_content, CAST(c1 AS VARCHAR)) AS INTEGER) FROM doc, char_rng),
                 1
             ) AS fl1,
             COALESCE(
                 (SELECT sl2 FROM symbol_focus),
                 (SELECT el2 FROM edge_focus),
                 (SELECT l2  FROM base),
                 (SELECT NULLIF(TRY_CAST(line_for_byte_offset(text_content, CAST(c2 AS VARCHAR)) AS INTEGER), 0) FROM doc, char_rng)
             ) AS fl2,
             COALESCE(
                 (SELECT sc1 FROM symbol_focus),
                 (SELECT ec1 FROM edge_focus),
                 (SELECT TRY_CAST(column_for_byte_offset(text_content, CAST(c1 AS VARCHAR)) AS INTEGER) FROM doc, char_rng)
             ) AS fc1,
             COALESCE(
                 (SELECT sc2 FROM symbol_focus),
                 (SELECT ec2 FROM edge_focus),
                 (SELECT TRY_CAST(column_for_byte_offset(text_content, CAST(c2 AS VARCHAR)) AS INTEGER) FROM doc, char_rng)
             ) AS fc2
     ),
    raw_text AS (
        SELECT
            CASE WHEN text_content IS NOT NULL THEN text_content
                 ELSE COALESCE(binary_preview(storage_uri, '4096'), '')
                END AS content
        FROM doc
    ),
    lines AS (
        SELECT
                ord::INTEGER AS ln,
                value AS line
         FROM raw_text
              CROSS JOIN UNNEST(string_split(content, CHR(10))) WITH ORDINALITY AS t(value, ord)
    ),
     win AS (
         SELECT
             -- If no fragment specified (frag is NULL or empty), show entire file
             CASE WHEN (SELECT frag FROM base) IS NULL OR (SELECT frag FROM base) = ''
                  THEN 1
                  ELSE GREATEST(1, COALESCE(fl1,1) - COALESCE(context_lines,3))
             END AS w1,
             CASE WHEN (SELECT frag FROM base) IS NULL OR (SELECT frag FROM base) = ''
                  THEN (SELECT MAX(ln) FROM lines)
                  ELSE COALESCE(COALESCE(fl2,fl1) + COALESCE(context_lines,3), 1 + COALESCE(context_lines,3)*2)
             END AS w2
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
