CREATE OR REPLACE VIEW markdown_headings AS
SELECT
  d.uri AS document_uri,
  CASE
    WHEN json_extract_string(h.properties, '$.slug') IS NOT NULL
    THEN d.uri || '#' || json_extract_string(h.properties, '$.slug')
    ELSE NULL
  END AS heading_uri,
  CAST(json_extract(h.properties, '$.level') AS INTEGER) AS level,
  json_extract_string(h.properties, '$.text') AS text,
  json_extract_string(h.properties, '$.slug') AS slug,
  s.start_line,
  s.end_line,
  s.start_column,
  s.end_column
FROM node h
JOIN edge e ON e.destination_node_id = h.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON h.span_id = s.id
WHERE h.kind = 'md_heading';

CREATE OR REPLACE VIEW markdown_links AS
SELECT
  d.uri AS document_uri,
  CASE
    WHEN s.start_line IS NOT NULL
    THEN d.uri || '#line=' || CAST(s.start_line AS VARCHAR)
    ELSE NULL
  END AS link_uri,
  json_extract_string(l.properties, '$.href') AS href,
  json_extract_string(l.properties, '$.text') AS link_text,
  json_extract_string(l.properties, '$.title') AS link_title,
  s.start_line,
  s.end_line,
  s.start_column,
  s.end_column
FROM node l
JOIN edge e ON e.destination_node_id = l.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON l.span_id = s.id
WHERE l.kind = 'md_link';

CREATE OR REPLACE VIEW markdown_capsules AS
SELECT
  d.uri AS document_uri,
  CASE
    WHEN json_extract_string(c.properties, '$.name') IS NOT NULL
    THEN d.uri || '#capsule=' || json_extract_string(c.properties, '$.name')
    ELSE NULL
  END AS capsule_uri,
  json_extract_string(c.properties, '$.name') AS name,
  json_extract_string(c.properties, '$.invariant') AS invariant,
  json_extract_string(c.properties, '$.example') AS example,
  CAST(json_extract(c.properties, '$.has_boundary') AS BOOLEAN) AS has_boundary,
  json_extract_string(c.properties, '$.boundary_text') AS boundary_text,
  json_extract(c.properties, '$.see_also') AS see_also,
  CAST(json_extract(c.properties, '$.heading_level') AS INTEGER) AS heading_level,
  c.headline,
  c.structure,
  s.start_line,
  s.end_line
FROM node c
JOIN edge e ON e.destination_node_id = c.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON c.span_id = s.id
WHERE c.kind = 'md_capsule';
