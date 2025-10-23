CREATE OR REPLACE MACRO annotations_for(u, kinds, min_severity) AS TABLE (
WITH doc AS (
    SELECT id AS doc_id FROM node
    WHERE lower(uri) = lower(repository_uri_container(u))
)
SELECT *
FROM annotations a, doc
WHERE a.scope_document_id = doc.doc_id
  AND (kinds IS NULL OR EXISTS (
    SELECT 1 FROM UNNEST(string_split(kinds, ',')) k(value)
    WHERE lower(trim(k.value)) = lower(a.kind)))
  AND (_severity_rank(a.severity) >= _severity_rank(COALESCE(min_severity,'hint')))
ORDER BY severity_rank DESC, a.created_at DESC
);