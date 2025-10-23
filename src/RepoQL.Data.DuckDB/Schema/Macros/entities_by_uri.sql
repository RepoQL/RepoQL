CREATE OR REPLACE MACRO entities_by_uri(u) AS TABLE (
WITH base AS (
    SELECT
        repository_uri_container(u)         AS base,
        repository_uri_fragment(u)          AS frag,
        repository_uri_fragment_kind(u)     AS kind,
        repository_uri_line_start(u)        AS l1,
        repository_uri_line_end(u)          AS l2
),
     char_rng AS (
         SELECT
             CASE WHEN kind='char' THEN try_cast(split_part(substr(frag, 6), ',', 1) AS BIGINT) END AS c1,
             CASE WHEN kind='char' THEN try_cast(NULLIF(split_part(substr(frag, 6), ',', 2), '') AS BIGINT) END AS c2
         FROM base
     )
SELECT
    'Document' AS entity, n.id AS id, n.kind AS aux,
    n.uri AS uri, n.uri AS container_uri, NULL AS fragment
FROM base b
         JOIN node n ON lower(n.uri) = lower(b.base)
WHERE b.frag IS NULL

UNION ALL
SELECT
    'Edge', e.id, e.type,
    repository_uri_join(n.uri, 'edge=' || CAST(e.id AS VARCHAR)),
    n.uri, 'edge=' || CAST(e.id AS VARCHAR)
FROM base b
         JOIN node n ON lower(n.uri) = lower(b.base)
         JOIN edge e ON e.scope_document_id = n.id
WHERE b.frag LIKE 'edge=%' AND substr(b.frag, 6) = CAST(e.id AS VARCHAR)

UNION ALL
SELECT
    'Span', s.id, NULL,
    repository_uri_join(n.uri, fragment_from_line_range(s.start_line, s.end_line)),
    n.uri, fragment_from_line_range(s.start_line, s.end_line)
FROM base b
         JOIN node n ON lower(n.uri) = lower(b.base)
         JOIN span s ON s.document_id = n.id
WHERE b.kind = 'line'
  AND s.start_line <= COALESCE(b.l1, s.start_line)
  AND s.end_line   >= COALESCE(b.l2, s.end_line)

UNION ALL
SELECT
    'Span', s.id, NULL,
    repository_uri_join(n.uri, fragment_from_char_range(s.start_byte, s.end_byte)),
    n.uri, fragment_from_char_range(s.start_byte, s.end_byte)
FROM base b, char_rng r
                 JOIN node n ON lower(n.uri) = lower(b.base)
                 JOIN span s ON s.document_id = n.id
WHERE b.kind = 'char'
  AND (r.c1 IS NOT NULL AND s.start_byte <= r.c1)
  AND (r.c2 IS NULL    OR  s.end_byte   >= r.c2)
);