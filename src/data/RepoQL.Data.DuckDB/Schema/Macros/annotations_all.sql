CREATE OR REPLACE MACRO annotations_all(kinds, min_severity) AS TABLE (
SELECT *
FROM annotations
WHERE (kinds IS NULL OR EXISTS (
    SELECT 1 FROM UNNEST(string_split(kinds, ',')) k(value)
    WHERE lower(trim(k.value)) = lower(annotations.kind)))
  AND (_severity_rank(severity) >= _severity_rank(COALESCE(min_severity,'hint')))
ORDER BY severity_rank DESC, annotations.created_at DESC
);