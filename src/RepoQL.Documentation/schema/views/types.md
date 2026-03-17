---
description: "Types(uri, file_uri, file_name, name, qualified_name, type_kind, namespace, visibility, signature, lang, extends, implements, start_line, end_line, headline, structure, node_id, span_id)"
tags: ["Types", "Classes", "Interfaces", "Structs", "Enums", "Inheritance"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Types View

All type definitions (classes, interfaces, structs, enums) across the codebase with inheritance info.

## Quick Reference

```sql
-- All types
SELECT name, type_kind, file_name FROM Types;

-- Classes in a namespace
SELECT qualified_name, extends FROM Types WHERE namespace = 'MyApp.Services';

-- Types implementing an interface
SELECT name, file_uri FROM Types WHERE implements::text LIKE '%IDisposable%';
```

---

## Capsule: TypesBasic

**Invariant**
`Types` extracts type definitions from nodes where `kind LIKE '%.type'`, exposing name, kind, inheritance, and location.

**Example**
```sql
SELECT name, type_kind, namespace FROM Types WHERE lang = 'csharp';
SELECT qualified_name, signature FROM Types WHERE type_kind = 'class';
SELECT * FROM Types WHERE file_name = 'UserService.cs';
```
//BOUNDARY: Only type-level nodes; methods/properties are in `Functions` view or raw `node` table.

**Depth**
- Filters nodes by `kind LIKE '%.type'` (e.g., `csharp.type`, `ts.type`, `py.type`)
- Properties extracted from node's JSON `properties` field
- `signature` falls back to `headline` if not set
- SeeAlso: `Functions` view for methods, `Files` view for documents

---

## Capsule: TypesInheritance

**Invariant**
`extends` shows base class; `implements` is a JSON array of interfaces.

**Example**
```sql
-- Find all classes extending a base
SELECT name, file_uri FROM Types WHERE extends = 'BaseService';

-- Types implementing multiple interfaces
SELECT name, implements FROM Types WHERE json_array_length(implements) > 1;

-- Interface hierarchy
SELECT name, extends FROM Types WHERE type_kind = 'interface';
```
//BOUNDARY: `implements` is JSON; use `::text LIKE` or `json_array_length()` for queries.

**Depth**
- `extends`: Single string (base class/interface name)
- `implements`: JSON array of interface names
- Both may be NULL for types with no inheritance
- Language-specific: C# has both; TypeScript uses `extends` for interfaces too

---

## Capsule: TypesLocation

**Invariant**
`start_line` gives the type's starting position; use `uri` fragment for precise addressing.

**Example**
```sql
-- Types with location info
SELECT name, file_name, start_line, end_line FROM Types WHERE start_line IS NOT NULL;

-- Largest types by line count
SELECT name, file_uri, (end_line - start_line) AS lines
FROM Types WHERE end_line IS NOT NULL ORDER BY lines DESC LIMIT 10;

-- Build URI with line range
SELECT name, file_uri || '#line=' || start_line || ',' || end_line AS type_uri FROM Types;

-- Preview type with snippet
SELECT name, s.text
FROM Types t, LATERAL snippet(t.uri, 3) s
WHERE t.name = 'AuthService' AND s.is_focus;
```

**Depth**
- `start_line`: First line of type definition (may be NULL)
- `end_line`: Last line of type definition (may be NULL)
- `uri`: Full URI with fragment for direct addressing
- Use `snippet(uri, context)` to preview type source code
- `span_id` links to full span details in `span` table

---

## Capsule: TypesFiltering

**Invariant**
Filter by language, visibility, kind, namespace, or file location.

**Example**
```sql
-- Public types only
SELECT name FROM Types WHERE visibility = 'public';

-- By language
SELECT name, type_kind FROM Types WHERE lang = 'csharp';
SELECT name, type_kind FROM Types WHERE lang = 'typescript';

-- By namespace pattern
SELECT name FROM Types WHERE namespace LIKE 'MyApp.%';

-- Exclude test types
SELECT name FROM Types WHERE file_uri NOT LIKE '%test%' AND file_uri NOT LIKE '%Test%';
```

**Depth**
- `lang`: Extracted from node kind prefix (`csharp`, `typescript`, etc.)
- `visibility`: `public`, `private`, `internal`, `protected`, or NULL
- `type_kind`: `class`, `interface`, `struct`, `enum`, `record`, etc.
- `namespace`: Fully qualified namespace or NULL

---

## Common Patterns

| Goal | Query |
|------|-------|
| All types | `SELECT * FROM Types` |
| By kind | `WHERE type_kind = 'class'` |
| By language | `WHERE lang = 'csharp'` |
| Public only | `WHERE visibility = 'public'` |
| In namespace | `WHERE namespace LIKE 'MyApp.%'` |
| With base class | `WHERE extends IS NOT NULL` |
| Implementing interface | `WHERE implements::text LIKE '%IFoo%'` |
| In specific file | `WHERE file_name = 'Foo.cs'` |
| By start line | `ORDER BY start_line` |

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `uri` | string | Full URI with fragment (file + line range) |
| `file_uri` | string | Parent file URI (no fragment) |
| `file_name` | string | Filename only |
| `name` | string | Simple type name |
| `qualified_name` | string | Fully qualified name with namespace |
| `type_kind` | string | `class`, `interface`, `struct`, `enum`, etc. |
| `namespace` | string | Containing namespace |
| `visibility` | string | `public`, `private`, `internal`, `protected` |
| `signature` | string | Type signature or headline |
| `lang` | string | Language (`csharp`, `typescript`, etc.) |
| `extends` | string | Base class/interface name |
| `implements` | json | Array of implemented interfaces |
| `start_line` | integer | First line of type definition (may be NULL) |
| `end_line` | integer | Last line of type definition (may be NULL) |
| `headline` | string | X-ray one-line summary |
| `structure` | string | X-ray detailed structure |
| `node_id` | uuid | Foreign key to `node` table |
| `span_id` | uuid | Foreign key to `span` table |
