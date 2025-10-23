CREATE OR REPLACE MACRO xray_lines(lod, include_kinds, max_per_document) AS TABLE (
WITH d AS (SELECT * FROM xray_documents()),
     i AS (SELECT * FROM xray_items(include_kinds, max_per_document))
SELECT file_name, 0 AS ord,
       (file_name || ' · ' || COALESCE(media_kind, media_base) ||
        CASE WHEN kinds_summary <> '' THEN '  ' || kinds_summary ELSE '' END) AS line
FROM d
UNION ALL
SELECT repository_uri_file_name(document_uri) AS file_name, 1 AS ord,
       ('  - ' || item_kind || ': ' || item_label || '  (' || item_uri || ')') AS line
FROM i
WHERE CAST(lod AS INTEGER) >= 1
);