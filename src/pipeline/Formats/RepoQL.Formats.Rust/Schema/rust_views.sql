CREATE OR REPLACE VIEW rust_types AS
SELECT
    n.properties->>'qualified_name' AS qualified_name,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.properties->>'name' END),
        MAX(n.properties->>'name')
    ) AS name,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.properties->>'kind' END),
        MAX(n.properties->>'kind')
    ) AS type_kind,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.properties->>'accessibility' END),
        MAX(n.properties->>'accessibility')
    ) AS visibility,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.properties->>'generics' END),
        MAX(n.properties->>'generics')
    ) AS generics,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.properties->>'derives' END),
        MAX(n.properties->>'derives')
    ) AS derives,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.properties->>'extends' END),
        MAX(n.properties->>'extends')
    ) AS supertraits,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN COALESCE(n.properties->>'is_unsafe', 'false') END),
        MAX(COALESCE(n.properties->>'is_unsafe', 'false')),
        'false'
    ) = 'true' AS is_unsafe,
    COUNT(DISTINCT doc.uri) AS definition_count,
    LIST(DISTINCT doc.uri) AS defined_in,
    COALESCE(
        MAX(CASE WHEN COALESCE(n.properties->>'is_stub', 'false') != 'true' THEN n.structure END),
        MAX(n.structure)
    ) AS structure
FROM node n
JOIN edge e ON e.destination_node_id = n.id
    AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE n.kind = 'rs.type'
GROUP BY n.properties->>'qualified_name';

CREATE OR REPLACE VIEW rust_functions AS
SELECT
    doc.uri AS file_uri,
    f.uri AS function_uri,
    f.headline,
    f.properties->>'name' AS name,
    f.properties->>'qualified_name' AS qualified_name,
    f.properties->>'accessibility' AS visibility,
    COALESCE(f.properties->>'is_async', 'false') = 'true' AS is_async,
    COALESCE(f.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    COALESCE(f.properties->>'is_const', 'false') = 'true' AS is_const,
    COALESCE(f.properties->>'is_test', 'false') = 'true' AS is_test,
    f.properties->>'generics' AS generics,
    f.properties->>'parameters' AS parameters,
    f.properties->>'return_type' AS return_type
FROM node f
JOIN edge fe ON fe.destination_node_id = f.id
    AND fe.type = 'HAS_PART' AND fe.is_composition = TRUE
JOIN node doc ON doc.id = fe.source_node_id AND doc.kind = 'document'
WHERE f.kind = 'rs.function';

CREATE OR REPLACE VIEW rust_methods AS
SELECT
    doc.uri AS file_uri,
    parent.uri AS parent_uri,
    parent.properties->>'name' AS parent_name,
    parent.properties->>'qualified_name' AS parent_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'qualified_name' AS qualified_name,
    m.properties->>'declaring_type' AS declaring_type,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_async', 'false') = 'true' AS is_async,
    COALESCE(m.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    COALESCE(m.properties->>'is_const', 'false') = 'true' AS is_const,
    COALESCE(m.properties->>'is_static', 'false') = 'true' AS is_static,
    m.properties->>'self_kind' AS self_kind,
    m.properties->>'parameters' AS parameters,
    m.properties->>'return_type' AS return_type,
    m.properties->>'impl_trait' AS impl_trait
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rs.member';

CREATE OR REPLACE VIEW rust_impls AS
SELECT
    src.uri AS type_uri,
    src.properties->>'name' AS target_type,
    src.properties->>'qualified_name' AS target_qualified_name,
    e.properties->>'target' AS trait_name,
    COALESCE(e.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    doc.uri AS file_uri
FROM edge e
JOIN node src ON src.id = e.source_node_id AND src.kind = 'rs.type'
JOIN edge de ON de.destination_node_id = src.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE e.type = 'IMPLEMENTS';

CREATE OR REPLACE VIEW rust_derives AS
SELECT
    src.uri AS type_uri,
    src.properties->>'name' AS type_name,
    src.properties->>'qualified_name' AS type_qualified_name,
    e.properties->>'target' AS derived_trait
FROM edge e
JOIN node src ON src.id = e.source_node_id AND src.kind = 'rs.type'
WHERE e.type = 'DERIVES';

CREATE OR REPLACE VIEW rust_modules AS
SELECT
    doc.uri AS file_uri,
    m.uri AS module_uri,
    m.properties->>'name' AS name,
    m.properties->>'qualified_name' AS qualified_name,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_inline', 'false') = 'true' AS is_inline
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node doc ON doc.id = me.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rs.module';

CREATE OR REPLACE VIEW rust_unsafe AS
SELECT
    'function' AS item_kind,
    f.name AS name,
    f.qualified_name AS qualified_name,
    f.file_uri AS file_uri
FROM rust_functions f
WHERE f.is_unsafe

UNION ALL

SELECT
    'method' AS item_kind,
    m.name AS name,
    m.qualified_name AS qualified_name,
    m.file_uri AS file_uri
FROM rust_methods m
WHERE m.is_unsafe

UNION ALL

SELECT
    'trait' AS item_kind,
    t.name AS name,
    t.qualified_name AS qualified_name,
    d.file_uri AS file_uri
FROM rust_types t
CROSS JOIN UNNEST(t.defined_in) AS d(file_uri)
WHERE t.type_kind = 'trait' AND t.is_unsafe

UNION ALL

SELECT
    'impl' AS item_kind,
    i.target_type AS name,
    i.target_qualified_name AS qualified_name,
    i.file_uri AS file_uri
FROM rust_impls i
WHERE i.is_unsafe;

CREATE OR REPLACE VIEW rust_imports AS
SELECT
    doc.uri AS file_uri,
    e.properties->>'path' AS import_path,
    e.properties->>'alias' AS alias,
    COALESCE(e.properties->>'is_glob', 'false') = 'true' AS is_glob,
    COALESCE(e.properties->>'is_pub', 'false') = 'true' AS is_reexport
FROM edge e
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE e.type = 'IMPORTS';

CREATE OR REPLACE VIEW rust_macros AS
SELECT
    doc.uri AS file_uri,
    m.uri AS macro_uri,
    m.properties->>'name' AS name,
    m.properties->>'qualified_name' AS qualified_name,
    m.properties->>'accessibility' AS visibility
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node doc ON doc.id = me.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rs.macro';

CREATE OR REPLACE VIEW rust_macro_expansion AS
SELECT
    doc.uri AS file_uri,
    a.rule_id AS macro_name,
    a.message AS description,
    s.start_line AS line
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
LEFT JOIN span s ON s.id = a.target_span_id
WHERE a.kind = 'rs.macro_expansion';
