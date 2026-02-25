CREATE OR REPLACE VIEW go_types AS
SELECT
    doc.uri AS document_uri,
    t.uri AS type_uri,
    t.properties->>'name' AS name,
    t.properties->>'qualified_name' AS qualified_name,
    t.properties->>'kind' AS type_kind,
    doc.properties->>'package_name' AS package_name,
    SUM(CASE WHEN json_extract_string(m.properties, '$.kind') = 'field' THEN 1 ELSE 0 END) AS field_count,
    SUM(CASE WHEN json_extract_string(m.properties, '$.kind') = 'method' THEN 1 ELSE 0 END) AS method_count
FROM node t
JOIN edge te ON te.destination_node_id = t.id
    AND te.type = 'HAS_PART' AND te.is_composition = TRUE
JOIN node doc ON doc.id = te.source_node_id
    AND doc.kind = 'document'
LEFT JOIN edge me ON me.source_node_id = t.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
LEFT JOIN node m ON m.id = me.destination_node_id
    AND m.kind = 'go.member'
WHERE t.kind = 'go.type'
GROUP BY
    doc.uri,
    t.uri,
    t.properties->>'name',
    t.properties->>'qualified_name',
    t.properties->>'kind',
    doc.properties->>'package_name';

CREATE OR REPLACE VIEW go_functions AS
SELECT
    doc.uri AS document_uri,
    doc.properties->>'package_name' AS package_name,
    f.uri AS function_uri,
    f.headline,
    f.properties->>'name' AS name,
    f.properties->>'qualified_name' AS qualified_name,
    f.properties->>'accessibility' AS visibility,
    f.properties->>'parameters' AS parameters,
    f.properties->>'return_type' AS return_type,
    f.properties->>'signature' AS signature
FROM node f
JOIN edge fe ON fe.destination_node_id = f.id
    AND fe.type = 'HAS_PART' AND fe.is_composition = TRUE
JOIN node doc ON doc.id = fe.source_node_id
    AND doc.kind = 'document'
WHERE f.kind = 'go.function'
  AND json_extract_string(f.properties, '$.kind') = 'function';

CREATE OR REPLACE VIEW go_methods AS
SELECT
    doc.uri AS document_uri,
    parent.uri AS type_uri,
    parent.properties->>'name' AS type_name,
    parent.properties->>'qualified_name' AS type_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'declaring_type' AS declaring_type,
    m.properties->>'receiver' AS receiver,
    m.properties->>'receiver_type' AS receiver_type,
    COALESCE(m.properties->>'is_pointer_receiver', 'false') = 'true' AS is_pointer_receiver,
    m.properties->>'accessibility' AS visibility,
    m.properties->>'parameters' AS parameters,
    m.properties->>'return_type' AS return_type,
    m.properties->>'signature' AS signature
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
    AND parent.kind = 'go.type'
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id
    AND doc.kind = 'document'
WHERE m.kind = 'go.member'
  AND json_extract_string(m.properties, '$.kind') = 'method'
UNION ALL
SELECT
    doc.uri AS document_uri,
    NULL AS type_uri,
    NULL AS type_name,
    NULL AS type_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'declaring_type' AS declaring_type,
    m.properties->>'receiver' AS receiver,
    m.properties->>'receiver_type' AS receiver_type,
    COALESCE(m.properties->>'is_pointer_receiver', 'false') = 'true' AS is_pointer_receiver,
    m.properties->>'accessibility' AS visibility,
    m.properties->>'parameters' AS parameters,
    m.properties->>'return_type' AS return_type,
    m.properties->>'signature' AS signature
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node doc ON doc.id = me.source_node_id
    AND doc.kind = 'document'
WHERE m.kind = 'go.member'
  AND json_extract_string(m.properties, '$.kind') = 'method';

CREATE OR REPLACE VIEW go_imports AS
SELECT
    doc.uri AS document_uri,
    doc.properties->>'package_name' AS package_name,
    e.properties->>'target' AS target,
    e.properties->>'alias' AS alias,
    e.properties->>'import_category' AS import_category
FROM edge e
JOIN node doc ON doc.id = e.source_node_id
    AND doc.kind = 'document'
WHERE e.type = 'IMPORTS'
  AND json_extract_string(doc.properties, '$.language') = 'go';

CREATE OR REPLACE VIEW go_fields AS
SELECT
    doc.uri AS document_uri,
    parent.uri AS type_uri,
    parent.properties->>'name' AS type_name,
    parent.properties->>'qualified_name' AS type_qualified_name,
    f.uri AS field_uri,
    f.properties->>'name' AS name,
    f.properties->>'field_type' AS field_type,
    f.properties->>'tag' AS tag,
    COALESCE(f.properties->>'is_embedded', 'false') = 'true' AS is_embedded,
    f.properties->>'accessibility' AS visibility
FROM node f
JOIN edge fe ON fe.destination_node_id = f.id
    AND fe.type = 'HAS_PART' AND fe.is_composition = TRUE
JOIN node parent ON parent.id = fe.source_node_id
    AND parent.kind = 'go.type'
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id
    AND doc.kind = 'document'
WHERE f.kind = 'go.member'
  AND json_extract_string(f.properties, '$.kind') = 'field';

CREATE OR REPLACE VIEW go_constants AS
SELECT
    c.uri AS uri,
    doc.uri AS document_uri,
    c.properties->>'name' AS name,
    c.properties->>'qualified_name' AS qualified_name,
    c.properties->>'const_type' AS const_type,
    c.properties->>'const_value' AS const_value,
    COALESCE(c.properties->>'is_exported', 'false') = 'true' AS is_exported,
    c.properties->>'enum_type' AS enum_type,
    s.start_line AS start_line,
    c.id AS node_id
FROM node c
JOIN edge ce ON ce.destination_node_id = c.id
    AND ce.type = 'HAS_PART' AND ce.is_composition = TRUE
JOIN node doc ON doc.id = ce.source_node_id
    AND doc.kind = 'document'
LEFT JOIN span s ON s.id = c.span_id
WHERE c.kind = 'go.member'
  AND json_extract_string(c.properties, '$.kind') = 'constant';

CREATE OR REPLACE VIEW go_variables AS
SELECT
    v.uri AS uri,
    doc.uri AS document_uri,
    v.properties->>'name' AS name,
    v.properties->>'qualified_name' AS qualified_name,
    v.properties->>'var_type' AS var_type,
    v.properties->>'var_value' AS var_value,
    COALESCE(v.properties->>'is_exported', 'false') = 'true' AS is_exported,
    COALESCE(v.properties->>'is_sentinel_error', 'false') = 'true' AS is_sentinel_error,
    COALESCE(v.properties->>'is_interface_assertion', 'false') = 'true' AS is_interface_assertion,
    v.properties->>'asserted_interface' AS asserted_interface,
    v.properties->>'asserted_type' AS asserted_type,
    s.start_line AS start_line,
    v.id AS node_id
FROM node v
JOIN edge ve ON ve.destination_node_id = v.id
    AND ve.type = 'HAS_PART' AND ve.is_composition = TRUE
JOIN node doc ON doc.id = ve.source_node_id
    AND doc.kind = 'document'
LEFT JOIN span s ON s.id = v.span_id
WHERE v.kind = 'go.member'
  AND json_extract_string(v.properties, '$.kind') = 'variable';

CREATE OR REPLACE VIEW go_enum_blocks AS
SELECT
    doc.uri AS document_uri,
    a.data->>'type_name' AS type_name,
    a.data->>'constant_names' AS constant_names,
    CAST(a.data->>'constant_count' AS INTEGER) AS constant_count
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id
    AND doc.kind = 'document'
WHERE a.kind = 'go.enum_block';

CREATE OR REPLACE VIEW go_tests AS
SELECT
    doc.uri AS document_uri,
    COALESCE(a.data->>'name', f.properties->>'name') AS function_name,
    a.data->>'test_kind' AS test_kind,
    a.data->>'tests_symbol' AS tests_symbol,
    COALESCE(target_span.start_line, function_span.start_line) AS start_line
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id
    AND doc.kind = 'document'
LEFT JOIN node f ON f.id = a.target_node_id
    AND f.kind = 'go.function'
LEFT JOIN span target_span ON target_span.id = a.target_span_id
LEFT JOIN span function_span ON function_span.id = f.span_id
WHERE a.kind = 'go.test';

CREATE OR REPLACE VIEW go_init_functions AS
SELECT
    doc.uri AS document_uri,
    doc.properties->>'package_name' AS package_name,
    f.properties->>'name' AS function_name,
    s.start_line AS start_line,
    f.id AS node_id
FROM node f
JOIN edge fe ON fe.destination_node_id = f.id
    AND fe.type = 'HAS_PART' AND fe.is_composition = TRUE
JOIN node doc ON doc.id = fe.source_node_id
    AND doc.kind = 'document'
LEFT JOIN span s ON s.id = f.span_id
WHERE f.kind = 'go.function'
  AND COALESCE(json_extract_string(f.properties, '$.is_init'), 'false') = 'true';

CREATE OR REPLACE VIEW go_directives AS
SELECT
    doc.uri AS document_uri,
    CASE a.kind
        WHEN 'go.build_constraint' THEN 'build'
        WHEN 'go.embed' THEN 'embed'
        WHEN 'go.generate' THEN 'generate'
        WHEN 'go.linkname' THEN 'linkname'
        WHEN 'go.goroutine' THEN 'goroutine'
        WHEN 'go.channel' THEN 'channel'
        WHEN 'go.select' THEN 'select'
        ELSE a.kind
    END AS directive_kind,
    COALESCE(a.data->>'directive_text', a.message) AS directive_text,
    s.start_line AS start_line
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id
    AND doc.kind = 'document'
LEFT JOIN span s ON s.id = a.target_span_id
WHERE a.kind IN (
    'go.build_constraint',
    'go.embed',
    'go.generate',
    'go.linkname',
    'go.goroutine',
    'go.channel',
    'go.select');

CREATE OR REPLACE VIEW go_embeds AS
SELECT
    source_type.uri AS struct_uri,
    source_type.properties->>'name' AS struct_name,
    e.properties->>'target' AS embedded_type,
    doc.uri AS document_uri
FROM edge e
JOIN node source_type ON source_type.id = e.source_node_id
    AND source_type.kind = 'go.type'
JOIN edge de ON de.destination_node_id = source_type.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id
    AND doc.kind = 'document'
WHERE e.type = 'EMBEDS';

CREATE OR REPLACE VIEW go_dependencies AS
SELECT
    doc.uri AS document_uri,
    e.properties->>'target' AS module_path,
    e.properties->>'version' AS version,
    COALESCE(e.properties->>'indirect', 'false') = 'true' AS is_indirect
FROM edge e
JOIN node doc ON doc.id = e.source_node_id
    AND doc.kind = 'document'
WHERE e.type = 'DEPENDS_ON'
  AND json_extract_string(doc.properties, '$.language') = 'go.mod';

CREATE OR REPLACE VIEW go_replaces AS
SELECT
    doc.uri AS document_uri,
    a.data->>'old_path' AS old_path,
    a.data->>'old_version' AS old_version,
    a.data->>'new_path' AS new_path,
    a.data->>'new_version' AS new_version,
    COALESCE(a.data->>'is_local_path', 'false') = 'true' AS is_local_path
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id
    AND doc.kind = 'document'
WHERE a.kind = 'go.mod_replace';

CREATE OR REPLACE VIEW go_implements AS
SELECT
    src.uri AS type_uri,
    src.properties->>'name' AS type_name,
    src.properties->>'qualified_name' AS type_qualified_name,
    dst.uri AS interface_uri,
    dst.properties->>'name' AS interface_name,
    dst.properties->>'qualified_name' AS interface_qualified_name,
    e.properties->>'receiver_kind' AS receiver_kind,
    COALESCE(e.properties->>'is_stdlib', 'false') = 'true' AS is_stdlib,
    e.properties->>'target' AS interface_target
FROM edge e
JOIN node src ON src.id = e.source_node_id AND src.kind = 'go.type'
LEFT JOIN node dst ON dst.id = e.destination_node_id AND dst.kind = 'go.type'
WHERE e.type = 'IMPLEMENTS';
