CREATE OR REPLACE VIEW ruby_types AS
SELECT
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'kind' AS type_kind,
    MAX(n.properties->>'extends') AS extends,
    COUNT(*) AS definition_count,
    LIST(doc.uri ORDER BY COALESCE(n.properties->>'is_reopening', 'false')) AS defined_in,
    MIN(CASE WHEN COALESCE(n.properties->>'is_reopening', 'false') != 'true' THEN doc.uri END) AS file_uri,
    MAX(n.structure) AS structure
FROM node n
JOIN edge e ON e.destination_node_id = n.id
    AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE n.kind = 'rb.type'
GROUP BY n.properties->>'qualified_name', n.properties->>'kind';

CREATE OR REPLACE VIEW ruby_methods AS
SELECT
    doc.uri AS file_uri,
    parent.uri AS type_uri,
    parent.properties->>'name' AS type_name,
    parent.properties->>'qualified_name' AS type_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_static', 'false') = 'true' AS is_class_method,
    COALESCE(m.properties->>'accepts_block', 'false') = 'true' AS accepts_block,
    COALESCE(m.properties->>'is_generated', 'false') = 'true' AS is_generated,
    m.properties->>'generator' AS generator,
    m.properties->>'parameters' AS parameters
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
    AND parent.kind = 'rb.type'
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rb.member';

CREATE OR REPLACE VIEW ruby_mixins AS
SELECT
    src.uri AS type_uri,
    repository_uri_container(src.uri) AS file_uri,
    src.properties->>'name' AS type_name,
    src.properties->>'qualified_name' AS type_qualified_name,
    src.properties->>'kind' AS type_kind,
    e.type AS mechanism,
    e.properties->>'target' AS module_name,
    CAST(e.properties->>'ordinal' AS INTEGER) AS mixin_order
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type IN ('INCLUDES', 'PREPENDS', 'EXTENDS_MODULE')
  AND src.kind = 'rb.type'
ORDER BY src.id, mixin_order;

CREATE OR REPLACE VIEW ruby_mro AS
SELECT
    type_uri,
    file_uri,
    type_name,
    type_qualified_name,
    module_name,
    mechanism,
    CASE mechanism
        WHEN 'PREPENDS' THEN 0
        WHEN 'INCLUDES' THEN 1
        WHEN 'EXTENDS_MODULE' THEN 2
    END AS mro_tier,
    mixin_order
FROM ruby_mixins
ORDER BY type_uri, mro_tier, mixin_order;

CREATE OR REPLACE VIEW ruby_inheritance AS
SELECT
    src.uri AS class_uri,
    repository_uri_container(src.uri) AS file_uri,
    src.properties->>'name' AS class_name,
    src.properties->>'qualified_name' AS qualified_name,
    e.properties->>'target' AS superclass_name
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type = 'EXTENDS' AND src.kind = 'rb.type';

CREATE OR REPLACE VIEW ruby_constants AS
SELECT
    doc.uri AS file_uri,
    parent.properties->>'qualified_name' AS namespace,
    c.uri AS constant_uri,
    c.properties->>'name' AS name,
    c.properties->>'qualified_name' AS qualified_name
FROM node c
JOIN edge ce ON ce.destination_node_id = c.id
    AND ce.type = 'HAS_PART' AND ce.is_composition = TRUE
JOIN node parent ON parent.id = ce.source_node_id
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE c.kind = 'rb.constant';

CREATE OR REPLACE VIEW ruby_requires AS
SELECT
    doc.uri AS file_uri,
    e.properties->>'path' AS required_path,
    COALESCE(e.properties->>'is_relative', 'false') = 'true' AS is_internal,
    CASE
        WHEN COALESCE(e.properties->>'is_relative', 'false') = 'true' THEN 'internal'
        ELSE 'external'
    END AS dependency_type
FROM edge e
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE e.type = 'REQUIRES';

CREATE OR REPLACE VIEW ruby_aliases AS
SELECT
    src.uri AS source_uri,
    repository_uri_container(src.uri) AS file_uri,
    src.properties->>'name' AS alias_name,
    e.properties->>'alias_type' AS alias_type,
    dst.properties->>'name' AS original_name,
    dst.uri AS original_uri
FROM edge e
JOIN node src ON src.id = e.source_node_id
LEFT JOIN node dst ON dst.id = e.destination_node_id
WHERE e.type = 'ALIASES';

CREATE OR REPLACE VIEW ruby_associations AS
SELECT
    src.uri AS model_uri,
    repository_uri_container(src.uri) AS file_uri,
    src.properties->>'name' AS model_name,
    src.properties->>'qualified_name' AS model_qualified_name,
    e.properties->>'association' AS association_type,
    e.properties->>'target' AS target_model
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type = 'ASSOCIATES' AND src.kind = 'rb.type';

CREATE OR REPLACE VIEW ruby_validations AS
SELECT
    doc.uri AS file_uri,
    a.rule_id AS field_name,
    a.message AS validation_rule,
    a.data->>'options' AS options
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
WHERE a.kind = 'ruby.validation';

CREATE OR REPLACE VIEW ruby_callbacks AS
SELECT
    doc.uri AS file_uri,
    a.rule_id AS callback_type,
    a.message AS callback_method,
    a.data->>'options' AS options
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
WHERE a.kind = 'ruby.callback';

CREATE OR REPLACE VIEW ruby_metaprogramming AS
SELECT
    doc.uri AS file_uri,
    a.message AS description,
    s.start_line AS line
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
LEFT JOIN span s ON s.id = a.target_span_id
WHERE a.kind = 'ruby.metaprogramming';
