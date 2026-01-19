-- C# structural views for querying types and members
-- These views expose indexed C# entities with strongly-typed columns

CREATE OR REPLACE VIEW csharp_namespaces AS
SELECT
    ns.id                AS namespace_id,
    doc.uri              AS document_uri,
    ns.properties->>'qualified_name' AS qualified_name,
    ns.properties->>'name'    AS name,
    ns.properties->>'parent_namespace_id' AS parent_namespace_id,
    ns.span_id,
    ns.properties AS properties
FROM node AS ns
LEFT JOIN span AS s ON s.id = ns.span_id
LEFT JOIN node AS doc ON doc.id = s.document_id
WHERE ns.kind = 'csharp.namespace';

CREATE OR REPLACE VIEW csharp_types AS
SELECT
    t.id                       AS type_id,
    doc.uri                    AS document_uri,
    t.properties->>'qualified_name' AS qualified_name,
    t.properties->>'name'           AS name,
    TRY_CAST(t.properties->>'kind' AS csharp_type_kind) AS kind,
    t.properties->>'namespace'      AS namespace,
    TRY_CAST(t.properties->>'accessibility' AS csharp_accessibility) AS accessibility,
    t.properties->>'extends'        AS extends,
    json_extract(t.properties, '$.implements') AS implements,
    json_extract(t.properties, '$.modifiers')  AS modifiers,
    TRY_CAST(t.properties->>'is_partial' AS BOOLEAN) AS is_partial,
    TRY_CAST(t.properties->>'is_static' AS BOOLEAN)  AS is_static,
    TRY_CAST(t.properties->>'is_record' AS BOOLEAN)  AS is_record,
    t.span_id,
    t.properties AS properties
FROM node AS t
LEFT JOIN span AS s ON s.id = t.span_id
LEFT JOIN node AS doc ON doc.id = s.document_id
WHERE t.kind = 'csharp.type';

CREATE OR REPLACE VIEW csharp_members AS
WITH parent_types AS (
    SELECT
        e.destination_node_id AS member_id,
        e.source_node_id      AS type_id
    FROM edge AS e
    WHERE e.is_composition = TRUE
)
SELECT
    m.id                      AS member_id,
    doc.uri                   AS document_uri,
    t.id                      AS declaring_type_id,
    t.properties->>'qualified_name' AS declaring_type,
    m.properties->>'name'          AS name,
    TRY_CAST(m.properties->>'kind' AS csharp_member_kind) AS kind,
    TRY_CAST(m.properties->>'accessibility' AS csharp_accessibility) AS accessibility,
    json_extract(m.properties, '$.modifiers') AS modifiers,
    TRY_CAST(m.properties->>'is_static' AS BOOLEAN) AS is_static,
    TRY_CAST(m.properties->>'is_async' AS BOOLEAN)  AS is_async,
    m.properties->>'return_type'   AS return_type,
    json_extract(m.properties, '$.parameters') AS parameters,
    m.span_id,
    m.properties AS properties
FROM node AS m
JOIN parent_types AS pt ON pt.member_id = m.id
JOIN node AS t ON t.id = pt.type_id AND t.kind = 'csharp.type'
LEFT JOIN span AS s ON s.id = m.span_id
LEFT JOIN node AS doc ON doc.id = s.document_id
WHERE m.kind = 'csharp.member';
