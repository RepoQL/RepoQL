CREATE OR REPLACE VIEW functions AS
SELECT
    -- Identity
    n.uri,
    repository_uri_container(n.uri) AS file_uri,
    repository_uri_file_name(repository_uri_container(n.uri)) AS file_name,

    -- Standard properties
    json_extract_string(n.properties, '$.name') AS name,
    COALESCE(
        json_extract_string(n.properties, '$.qualified_name'),
        CASE WHEN json_extract_string(n.properties, '$.declaring_type') IS NOT NULL
             THEN json_extract_string(n.properties, '$.declaring_type') || '.' || json_extract_string(n.properties, '$.name')
             ELSE json_extract_string(n.properties, '$.name')
        END
    ) AS qualified_name,
    json_extract_string(n.properties, '$.kind') AS function_kind,
    json_extract_string(n.properties, '$.declaring_type') AS declaring_type,
    json_extract_string(n.properties, '$.accessibility') AS visibility,
    COALESCE(json_extract_string(n.properties, '$.signature'), n.headline) AS signature,
    json_extract_string(n.properties, '$.return_type') AS return_type,
    json_extract(n.properties, '$.parameters') AS parameters,

    -- Language
    split_part(n.kind, '.', 1) AS lang,

    -- Modifiers
    COALESCE(json_extract_string(n.properties, '$.is_static'), 'false') = 'true' AS is_static,
    COALESCE(json_extract_string(n.properties, '$.is_async'), 'false') = 'true' AS is_async,

    -- Location
    TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER) AS start_line,
    TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER) AS end_line,

    -- X-ray
    n.headline,
    n.structure,

    -- Join keys
    n.id AS node_id,
    n.span_id
FROM node n
WHERE n.kind IN ('csharp.member', 'typescript.member', 'typescript.function', 'php.member', 'php.function')
  AND json_extract_string(n.properties, '$.kind') IN ('method', 'constructor', 'function');
