CREATE OR REPLACE VIEW typescript_imports AS
SELECT
    d.uri AS file_uri,
    json_extract_string(i.import_json, '$.specifier') AS specifier,
    json_extract_string(i.import_json, '$.kind') AS import_kind,
    json_extract_string(i.import_json, '$.style') AS import_style,
    CASE
        WHEN json_extract_string(i.import_json, '$.specifier') LIKE '.%' THEN 'relative'
        ELSE 'package'
    END AS source_kind
FROM node AS d
CROSS JOIN LATERAL (
    SELECT value AS import_json
    FROM json_each(COALESCE(json_extract(d.properties, '$.imports'), '[]'::JSON))
) AS i
WHERE d.kind = 'document'
  AND media_type_kind(json_extract_string(d.properties, '$.media_type')) IN (
      'code.typescript',
      'code.typescript.react',
      'code.javascript',
      'code.javascript.react'
  );

CREATE OR REPLACE VIEW typescript_declarations AS
SELECT
    doc.uri AS file_uri,
    decl.uri AS declaration_uri,
    decl.headline,
    json_extract_string(decl.properties, '$.name') AS name,
    COALESCE(
        json_extract_string(decl.properties, '$.kind'),
        json_extract_string(decl.properties, '$.decl_kind')
    ) AS decl_kind,
    (
        COALESCE(json_extract_string(decl.properties, '$.is_exported'), 'false') = 'true'
        OR COALESCE(json_extract_string(decl.properties, '$.accessibility'), '') = 'export'
    ) AS is_exported,
    json_extract_string(decl.properties, '$.export_kind') AS export_kind,
    decl.structure
FROM node AS decl
JOIN edge AS e ON e.destination_node_id = decl.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node AS doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE decl.kind IN ('typescript.type', 'typescript.function', 'ts_decl_variable', 'ts_decl_namespace');

CREATE OR REPLACE VIEW typescript_components AS
SELECT
    doc.uri AS file_uri,
    comp.uri AS component_uri,
    comp.headline,
    json_extract_string(comp.properties, '$.name') AS name,
    json_extract_string(comp.properties, '$.kind') AS decl_kind,
    comp.structure
FROM node AS comp
JOIN edge AS e ON e.destination_node_id = comp.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node AS doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE comp.kind IN ('typescript.type', 'typescript.function', 'ts_decl_variable', 'ts_decl_namespace')
  AND COALESCE(json_extract_string(comp.properties, '$.is_component'), 'false') = 'true';

CREATE OR REPLACE VIEW typescript_members AS
SELECT
    doc.uri AS file_uri,
    parent.uri AS type_uri,
    json_extract_string(parent.properties, '$.name') AS type_name,
    member.uri AS member_uri,
    json_extract_string(member.properties, '$.name') AS member_name,
    json_extract_string(member.properties, '$.kind') AS member_kind,
    json_extract_string(member.properties, '$.return_type') AS return_type,
    json_extract_string(member.properties, '$.type') AS type,
    json_extract_string(member.properties, '$.parameters') AS parameters
FROM node AS member
JOIN edge AS me ON me.destination_node_id = member.id AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node AS parent ON parent.id = me.source_node_id AND parent.kind = 'typescript.type'
JOIN edge AS de ON de.destination_node_id = parent.id AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node AS doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE member.kind = 'typescript.member';
