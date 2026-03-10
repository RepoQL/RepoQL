CREATE OR REPLACE VIEW python_types AS
SELECT
    doc.uri AS file_uri,
    n.uri AS type_uri,
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'type_kind' AS type_kind,
    n.properties->>'extends' AS extends,
    n.properties->>'metaclass' AS metaclass,
    COALESCE(n.properties->>'is_abstract', 'false') = 'true' AS is_abstract,
    n.properties->>'decorators' AS decorators,
    n.properties->>'docstring' AS docstring,
    n.properties->>'slots' AS slots,
    n.properties->'variables' AS variables,
    n.structure AS structure
FROM node n
JOIN edge e ON e.destination_node_id = n.id
    AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE n.kind = 'py.type';

CREATE OR REPLACE VIEW python_methods AS
SELECT
    doc.uri AS file_uri,
    parent.uri AS type_uri,
    parent.properties->>'name' AS type_name,
    parent.properties->>'qualified_name' AS type_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'kind' AS method_kind,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_static', 'false') = 'true' AS is_static,
    COALESCE(m.properties->>'is_classmethod', 'false') = 'true' AS is_classmethod,
    COALESCE(m.properties->>'is_async', 'false') = 'true' AS is_async,
    COALESCE(m.properties->>'is_generator', 'false') = 'true' AS is_generator,
    COALESCE(m.properties->>'uses_async_with', 'false') = 'true' AS uses_async_with,
    COALESCE(m.properties->>'uses_async_for', 'false') = 'true' AS uses_async_for,
    COALESCE(m.properties->>'is_generated', 'false') = 'true' AS is_generated,
    COALESCE(m.properties->>'is_overload', 'false') = 'true' AS is_overload,
    m.properties->>'generator' AS generator,
    m.properties->>'parameters' AS parameters,
    m.properties->>'return_type' AS return_type,
    m.properties->>'decorators' AS decorators,
    m.properties->>'docstring' AS docstring
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
    AND parent.kind = 'py.type'
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'py.member';

CREATE OR REPLACE VIEW python_imports AS
SELECT
    doc.uri AS file_uri,
    e.properties->>'specifier' AS specifier,
    e.properties->>'names' AS imported_names,
    COALESCE(e.properties->>'is_relative', 'false') = 'true' AS is_relative,
    CAST(COALESCE(e.properties->>'relative_level', '0') AS INTEGER) AS relative_level,
    COALESCE(e.properties->>'is_type_checking_only', 'false') = 'true' AS is_type_checking_only,
    CASE
        WHEN COALESCE(e.properties->>'is_relative', 'false') = 'true' THEN 'internal'
        ELSE 'unknown'
    END AS dependency_type
FROM edge e
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE e.type = 'IMPORTS'
  AND json_extract_string(doc.properties, '$.language') = 'python';
