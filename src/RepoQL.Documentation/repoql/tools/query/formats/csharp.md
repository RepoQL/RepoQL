# C# Quick Reference

## Views

```sql
csharp_namespaces(namespace_id, document_uri, qualified_name, name, parent_namespace_id, span_id, properties)
csharp_types(type_id, document_uri, qualified_name, name, kind, namespace, accessibility, base_type, interfaces, is_partial, is_static, is_record, span_id, properties)
csharp_members(member_id, document_uri, declaring_type_id, declaring_type, name, kind, accessibility, is_static, is_async, return_type, parameters, span_id, properties)
```

All views include `properties->>'symbol_key'` for stable symbol identification.

## Queries

```sql
-- Find all public interfaces
SELECT qualified_name, document_uri
FROM csharp_types
WHERE kind = 'interface' AND accessibility = 'public'

-- Find async methods
SELECT declaring_type || '.' || name, return_type
FROM csharp_members
WHERE is_async = true

-- Find implementations of an interface
SELECT t.qualified_name, t.document_uri
FROM csharp_types t
JOIN edge e ON e.source_node_id = t.type_id
WHERE e.type = 'IMPLEMENTS'
  AND e.properties->>'to_symbol_key' LIKE '%IRepository%'

-- Find all usages of a type
WITH target AS (
  SELECT type_id, properties->>'symbol_key' AS key
  FROM csharp_types
  WHERE qualified_name = 'MyApp.Services.UserService'
)
SELECT doc.uri, s.start_line
FROM edge e
JOIN target t ON e.properties->>'to_symbol_key' = t.key
JOIN span s ON s.id = e.properties->>'from_span_id'::UUID
JOIN node doc ON doc.id = s.document_id
WHERE e.type = 'USES_SYMBOL'

-- Find partial types
SELECT qualified_name, COUNT(*) AS part_count
FROM csharp_types
WHERE is_partial = true
GROUP BY properties->>'symbol_key', qualified_name

-- Find methods with many parameters
SELECT declaring_type || '.' || name, JSON_ARRAY_LENGTH(parameters) AS params
FROM csharp_members
WHERE kind = 'method' AND JSON_ARRAY_LENGTH(parameters) > 5
ORDER BY params DESC

-- Search → structure → snippet
WITH hits AS (
  SELECT uri FROM file_search('repository service', question := 'Where is data access implemented?', k := 5)
)
SELECT h.uri, t.qualified_name, t.kind, sn.line_number, sn.text
FROM hits h
JOIN csharp_types t ON t.document_uri = h.uri
JOIN LATERAL snippet(h.uri || '#line=' || (
  SELECT s.start_line FROM span s WHERE s.id = t.span_id
), 3) sn ON true
WHERE t.accessibility = 'public'
ORDER BY h.uri, t.qualified_name
```

## URIs

```sql
-- Symbol-based URIs (deterministic across partial definitions)
-- file:///src/Services/UserService.cs#symbol=MyApp.Services.UserService&line=42,60

-- Resolve location
SELECT * FROM entities_by_uri('file:///src/Domain/User.cs#symbol=User.Name')

-- Preview
SELECT line_number, text
FROM snippet('file:///src/Services/UserService.cs#line=42', 5)
```

## X-Ray

```sql
-- Understand file contents without reading
SELECT headline, summary, structure
FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///src/Services/UserService.cs'

-- Quick inventory
SELECT * FROM Files WHERE name LIKE '%.cs'

-- Structural preview (types and members)
SELECT n.uri, n.kind, n.properties
FROM node n
JOIN node doc ON doc.id = n.id OR EXISTS (
  SELECT 1 FROM edge e WHERE e.source_node_id = doc.id AND e.destination_node_id = n.id
)
WHERE doc.uri = 'file:///src/Services/UserService.cs'
  AND n.kind IN ('csharp.type', 'csharp.member')
```

## Analysis Modes

**Fast Mode** (no .csproj found):
- Syntax parsing only
- Namespaces, types, members extracted
- No semantic analysis, no diagnostics

**Project Mode** (.csproj detected):
- Full semantic analysis via MSBuild workspace
- Symbol resolution, cross-file references
- Source generators executed
- Analyzers run, diagnostics emitted

Check mode: `SELECT properties->>'analysis_mode' FROM node WHERE kind = 'document'`

## Lint Rules

All C# diagnostics prefixed with `csharp/`:
- `csharp/CS0103` — Name does not exist in context
- `csharp/CS8618` — Non-nullable field uninitialized
- `csharp/CA1806` — Do not ignore method results
- + all compiler and analyzer rules

```sql
-- Find all errors
SELECT rule_id, message, target_uri, data->>'help_link'
FROM annotation
WHERE kind = 'lint'
  AND rule_id LIKE 'csharp/%'
  AND severity = 'error'

-- Configure via .editorconfig
# [*.cs]
# dotnet_diagnostic.CS0618.severity = none
```

## Node Kinds

- `csharp.namespace` — Namespace declaration
- `csharp.type` — Class, interface, struct, enum, record, delegate
- `csharp.member` — Method, property, field, event, constructor
- `csharp.attribute` — Attribute annotation
- `csharp.using` — Using directive
- `csharp.generated_document` — Source generator output

## Edge Types

- `HAS_PART` — Composition (document→namespace→type→member)
- `DECLARES_SYMBOL` — Symbol declaration
- `INHERITS_FROM` — Base class inheritance
- `IMPLEMENTS` — Interface implementation
- `ANNOTATED_WITH` — Attribute application
- `USES_SYMBOL` — Symbol reference (method call, field access, type usage)

## Symbol Keys

Stable identifiers from Roslyn `SymbolKey` API:
- Deterministic across runs
- Same key for partial type parts
- Enables cross-file symbol tracking

```sql
-- Group partial types by symbol
SELECT properties->>'symbol_key', STRING_AGG(document_uri, ', ')
FROM csharp_types
WHERE is_partial = true
GROUP BY properties->>'symbol_key'
```
