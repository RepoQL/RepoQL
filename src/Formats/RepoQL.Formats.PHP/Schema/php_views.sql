CREATE OR REPLACE VIEW php_types AS
SELECT
    doc.uri AS document_uri,
    t.uri AS type_uri,
    t.headline,
    json_extract_string(t.properties, '$.name') AS name,
    json_extract_string(t.properties, '$.qualified_name') AS qualified_name,
    json_extract_string(t.properties, '$.kind') AS type_kind,
    json_extract_string(t.properties, '$.accessibility') AS accessibility,
    COALESCE(json_extract_string(t.properties, '$.is_abstract'), 'false') = 'true' AS is_abstract,
    COALESCE(json_extract_string(t.properties, '$.is_final'), 'false') = 'true' AS is_final,
    json_extract_string(t.properties, '$.extends') AS extends,
    json_extract_string(t.properties, '$.backed_type') AS backed_type,
    t.structure
FROM node AS t
JOIN edge AS e ON e.destination_node_id = t.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node AS doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE t.kind = 'php.type';

CREATE OR REPLACE VIEW php_members AS
SELECT
    doc.uri AS document_uri,
    parent.uri AS type_uri,
    json_extract_string(parent.properties, '$.name') AS type_name,
    member.uri AS member_uri,
    member.headline,
    json_extract_string(member.properties, '$.name') AS name,
    json_extract_string(member.properties, '$.kind') AS member_kind,
    json_extract_string(member.properties, '$.accessibility') AS accessibility,
    COALESCE(json_extract_string(member.properties, '$.is_static'), 'false') = 'true' AS is_static,
    json_extract_string(member.properties, '$.return_type') AS return_type,
    json_extract_string(member.properties, '$.type') AS type
FROM node AS member
JOIN edge AS me ON me.destination_node_id = member.id AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node AS parent ON parent.id = me.source_node_id AND parent.kind = 'php.type'
JOIN edge AS de ON de.destination_node_id = parent.id AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node AS doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE member.kind IN ('php.member', 'php.property', 'php.constant', 'php.enum_case');

CREATE OR REPLACE VIEW php_trait_usage AS
SELECT
    t.uri AS type_uri,
    json_extract_string(t.properties, '$.name') AS type_name,
    json_extract_string(t.properties, '$.kind') AS type_kind,
    json_extract_string(e.properties, '$.target') AS trait_name
FROM edge AS e
JOIN node AS t ON t.id = e.source_node_id
WHERE e.type = 'USES_TRAIT';

CREATE OR REPLACE VIEW php_inheritance AS
SELECT
    src.uri AS source_uri,
    json_extract_string(src.properties, '$.name') AS source_name,
    json_extract_string(src.properties, '$.kind') AS source_kind,
    e.type AS relationship,
    json_extract_string(e.properties, '$.target') AS target_name
FROM edge AS e
JOIN node AS src ON src.id = e.source_node_id
WHERE e.type IN ('EXTENDS', 'IMPLEMENTS', 'USES_TRAIT')
  AND src.kind = 'php.type';
