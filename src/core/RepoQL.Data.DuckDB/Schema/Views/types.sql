CREATE OR REPLACE VIEW types AS
SELECT
    -- Identity
    n.uri,
    repository_uri_container(n.uri) AS file_uri,
    repository_uri_file_name(repository_uri_container(n.uri)) AS file_name,

    -- Standard properties
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'kind' AS type_kind,
    n.properties->>'namespace' AS namespace,
    n.properties->>'accessibility' AS visibility,
    COALESCE(n.properties->>'signature', n.headline) AS signature,

    -- Language from node kind
    split_part(n.kind, '.', 1) AS lang,

    -- Inheritance
    n.properties->>'extends' AS extends,
    n.properties->'implements' AS implements,

    -- Location
    TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER) AS start_line,
    TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER) AS end_line,

    -- X-ray summaries
    n.headline,
    n.structure,

    -- Join keys
    n.id AS node_id,
    n.span_id
FROM node n
WHERE n.kind LIKE '%.type';
