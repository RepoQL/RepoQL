CREATE OR REPLACE MACRO xray_documents() AS TABLE (
WITH docs AS (
    SELECT id, uri, artifact_id FROM node WHERE kind = 'document'
),
     media AS (
         SELECT d.id AS doc_id, a.media_type, a.byte_size
         FROM docs d LEFT JOIN artifact a ON a.id = d.artifact_id
     ),
     parts AS (
         SELECT e.source_node_id AS doc_id, c.kind, COUNT(*) AS item_count
         FROM edge e
                  JOIN node c ON c.id = e.destination_node_id
         WHERE e.is_composition = TRUE
         GROUP BY 1,2
     ),
     kinds AS (
         SELECT doc_id, string_agg(kind || ':' || CAST(item_count AS TEXT), ' ') AS kinds_summary
         FROM parts GROUP BY doc_id
     )
SELECT
    d.uri                                        AS document_uri,
    repository_uri_file_name(d.uri)              AS file_name,
    media_type_base(m.media_type)                AS media_base,
    media_type_kind(m.media_type)                AS media_kind,
    m.byte_size                                  AS byte_size,
    COALESCE(k.kinds_summary, '')                AS kinds_summary,
    a.headline                                   AS headline,
    a.summary                                    AS summary,
    a.structure                                  AS structure
FROM docs d
         LEFT JOIN media m ON m.doc_id = d.id
         LEFT JOIN kinds k ON k.doc_id = d.id
         LEFT JOIN artifact a ON a.id = d.artifact_id
ORDER BY lower(file_name)
);