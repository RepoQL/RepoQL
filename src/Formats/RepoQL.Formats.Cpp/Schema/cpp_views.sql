-- cpp_classes: All class/struct/union declarations (C++ specific projection over Types)
CREATE OR REPLACE VIEW cpp_classes AS
SELECT
    n.uri,
    repository_uri_container(n.uri) AS file_uri,
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'kind' AS type_kind,  -- 'class', 'struct', 'union'
    n.properties->>'accessibility' AS default_access,
    n.properties->>'extends' AS extends,
    n.properties->>'is_abstract' AS is_abstract,
    TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER) AS start_line,
    TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER) AS end_line,
    n.headline,
    n.id AS node_id, n.span_id
FROM node n
WHERE n.kind = 'cpp.type'
  AND n.properties->>'kind' IN ('class', 'struct', 'union');

-- cpp_functions: All function declarations and definitions (C++ specific projection over Functions)
CREATE OR REPLACE VIEW cpp_functions AS
SELECT
    n.uri,
    repository_uri_container(n.uri) AS file_uri,
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'declaring_type' AS declaring_type,
    n.properties->>'return_type' AS return_type,
    n.properties->>'accessibility' AS access,
    COALESCE(n.properties->>'signature', n.headline) AS signature,
    COALESCE(n.properties->>'is_virtual', 'false') = 'true' AS is_virtual,
    COALESCE(n.properties->>'is_pure_virtual', 'false') = 'true' AS is_pure_virtual,
    COALESCE(n.properties->>'is_noexcept', 'false') = 'true' AS is_noexcept,
    COALESCE(n.properties->>'is_constexpr', 'false') = 'true' AS is_constexpr,
    COALESCE(n.properties->>'is_static', 'false') = 'true' AS is_static,
    TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER) AS start_line,
    TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER) AS end_line,
    n.headline,
    n.id AS node_id, n.span_id
FROM node n
WHERE n.kind IN ('cpp.member', 'cpp.function')
  AND n.properties->>'kind' IN ('method', 'constructor', 'function');

-- cpp_includes: Include graph
CREATE OR REPLACE VIEW cpp_includes AS
SELECT
    n.properties->>'target' AS target_header,
    n.properties->>'style' AS include_style,  -- '<>' or '""'
    repository_uri_container(n.uri) AS source_uri,
    n.id AS node_id
FROM node n
WHERE n.kind = 'cpp.include';

-- cpp_templates: Template declarations and specializations
CREATE OR REPLACE VIEW cpp_templates AS
SELECT
    n.uri,
    n.properties->>'name' AS name,
    n.properties->>'template_params' AS template_params,
    n.properties->>'base_template' AS base_template,
    n.properties->>'specialization_args' AS template_args,
    CASE WHEN n.properties->>'base_template' IS NOT NULL THEN 'specialization' ELSE 'primary' END AS template_kind,
    repository_uri_container(n.uri) AS file_uri,
    n.id AS node_id
FROM node n
WHERE n.properties->>'is_template' = 'true'
  AND n.kind LIKE 'cpp.%';

-- cpp_enums: Enum declarations
CREATE OR REPLACE VIEW cpp_enums AS
SELECT
    n.uri,
    n.properties->>'name' AS name,
    n.properties->>'is_scoped' AS is_scoped,
    n.properties->>'underlying_type' AS underlying_type,
    repository_uri_container(n.uri) AS file_uri,
    n.id AS node_id
FROM node n
WHERE n.kind = 'cpp.type'
  AND n.properties->>'kind' = 'enum';

-- cpp_macro_invocations: Known macro call sites (from annotations)
CREATE OR REPLACE VIEW cpp_macro_invocations AS
SELECT
    an.id,
    an.message,
    json_extract_string(an.data, '$.macro_name') AS name,
    json_extract_string(an.data, '$.context') AS context,
    repository_uri_container(doc.uri) AS file_uri,
    TRY_CAST(json_extract_string(an.data, '$.start_line') AS INTEGER) AS start_line,
    TRY_CAST(json_extract_string(an.data, '$.end_line') AS INTEGER) AS end_line,
    an.target_span_id AS span_id
FROM annotation an
JOIN node doc ON doc.id = an.scope_document_id
WHERE an.rule_id = 'cpp/macro_interference';

-- cpp_namespace_members: Unified namespace view across all files
CREATE OR REPLACE VIEW cpp_namespace_members AS
SELECT
    n.properties->>'namespace' AS namespace,
    n.properties->>'name' AS name,
    n.properties->>'kind' AS member_kind,
    n.properties->>'accessibility' AS accessibility,
    repository_uri_container(n.uri) AS file_uri,
    n.id AS node_id
FROM node n
WHERE n.kind LIKE 'cpp.%'
  AND n.properties->>'namespace' IS NOT NULL;
