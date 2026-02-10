CREATE OR REPLACE VIEW pdf_bookmarks AS
SELECT
    n.id,
    parent.uri AS document_uri,
    n.properties->>'title' AS title,
    CAST(n.properties->>'level' AS INTEGER) AS level,
    CAST(n.properties->>'target_page' AS INTEGER) AS target_page,
    s.start_line AS start_page,
    s.end_line AS end_page,
    parent.headline AS document_headline
FROM node n
JOIN edge e ON e.destination_node_id = n.id AND e.type = 'HAS_PART'
JOIN node parent ON parent.id = e.source_node_id AND parent.kind = 'document'
LEFT JOIN span s ON s.id = n.span_id
WHERE n.kind = 'pdf_bookmark';

CREATE OR REPLACE VIEW pdf_form_fields AS
SELECT
    n.id,
    parent.uri AS document_uri,
    n.properties->>'field_name' AS field_name,
    n.properties->>'field_type' AS field_type,
    n.properties->>'value' AS value,
    CAST(n.properties->>'page' AS INTEGER) AS page,
    parent.headline AS document_headline
FROM node n
JOIN edge e ON e.destination_node_id = n.id AND e.type = 'HAS_PART'
JOIN node parent ON parent.id = e.source_node_id AND parent.kind = 'document'
WHERE n.kind = 'pdf_form_field';

CREATE OR REPLACE VIEW pdf_annotations AS
SELECT
    a.id,
    doc.uri AS document_uri,
    a.kind AS annotation_type,
    CAST(json_extract_string(a.data, '$.page') AS INTEGER) AS page,
    a.message AS content,
    json_extract_string(a.data, '$.author') AS author,
    json_extract_string(a.data, '$.date') AS date,
    doc.headline AS document_headline
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
WHERE a.source = 'repoql.formats.pdf';
