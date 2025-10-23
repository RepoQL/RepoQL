CREATE OR REPLACE MACRO xray_items(include_kinds, max_per_document) AS TABLE (
WITH docs AS (SELECT id, uri FROM node WHERE kind = 'document'),
     cand AS (SELECT d.id                                                                                            AS doc_id,
                     d.uri                                                                                           AS document_uri,
                     c.id                                                                                            AS item_id,
                     c.kind                                                                                          AS item_kind,
                     node_display_label(c.kind, c.properties)                                                        AS item_label,
                     s.start_line,
                     s.end_line,
                     s.start_byte,
                     s.end_byte,
                     e.ordinal,
                     node_primary_fragment(c.kind, c.properties, s.start_line, s.end_line, s.start_byte,
                                           s.end_byte)                                                               AS frag
              FROM docs d
                       JOIN edge e ON e.source_node_id = d.id AND e.is_composition = TRUE
                       JOIN node c ON c.id = e.destination_node_id
                       LEFT JOIN span s ON s.id = c.span_id
              WHERE include_kinds IS NULL
                 OR EXISTS (SELECT 1
                            FROM UNNEST(string_split(include_kinds, ',')) k(value)
                            WHERE lower(trim(k.value)) = lower(c.kind))),
     ranked AS (SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY doc_id
                           ORDER BY COALESCE(start_line, 2147483647), COALESCE(ordinal, 2147483647), item_id
                           ) AS rn
                FROM cand)
SELECT document_uri,
       repository_uri_file_name(document_uri)                          AS file_name,
       item_kind,
       COALESCE(item_label, '?')                                       AS item_label,
       COALESCE(repository_uri_join(document_uri, frag), document_uri) AS item_uri
FROM ranked
WHERE rn <= COALESCE(CAST(max_per_document AS INTEGER), 8)
ORDER BY lower(file_name), rn 
);