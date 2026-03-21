---
description: "Functions(uri, file_uri, file_name, name, qualified_name, function_kind, declaring_type, visibility, signature, return_type, parameters, lang, is_static, is_async, start_line, end_line, headline, structure, node_id, span_id)"
tags: ["Functions", "Methods", "Constructors", "Parameters", "Signatures"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Functions View

All function and method definitions with signatures, parameters, and modifiers.

## Quick Reference

```sql
-- All functions
SELECT name, declaring_type, return_type FROM Functions;

-- Methods in a class
SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';

-- Async methods
SELECT qualified_name, file_name FROM Functions WHERE is_async = true;
```

---

## Capsule: FunctionsBasic

**Invariant**
`Functions` extracts methods, constructors, and functions from nodes, exposing signature, parameters, and modifiers.

**Example**
```sql
SELECT name, function_kind, declaring_type FROM Functions WHERE lang = 'csharp';
SELECT qualified_name, return_type FROM Functions WHERE function_kind = 'method';
SELECT * FROM Functions WHERE file_name = 'UserService.cs';
```
//BOUNDARY: Includes methods, constructors, functions. Excludes properties, fields, type definitions.

**Depth**
- Filters: `csharp.member`, `typescript.member`, `typescript.function`, `php.member`, `php.function`, `py.member`, `py.function` with kind in `method`, `constructor`, `function`
- `qualified_name` computed as `declaring_type.name` if not explicitly set
- `signature` falls back to `headline` if not set
- SeeAlso: `Types` view for classes, `Files` view for documents
- For richer language-specific views with edges and relationships, see `help:///formats/` — e.g. `csharp_members`, `go_functions`, `ruby_methods`

---

## Capsule: FunctionsSignature

**Invariant**
`signature` shows the full method signature; `parameters` is a JSON array of parameter details.

**Example**
```sql
-- Find by signature pattern
SELECT name, signature FROM Functions WHERE signature LIKE '%async%Task%';

-- Methods with many parameters
SELECT name, json_array_length(parameters) as param_count
FROM Functions ORDER BY param_count DESC LIMIT 10;

-- Parameter details
SELECT name, parameters FROM Functions WHERE name = 'ProcessOrder';
```
//BOUNDARY: `parameters` is JSON array; use `json_array_length()` or `::text LIKE` for queries.

**Depth**
- `signature`: Full signature string from properties or headline
- `parameters`: JSON array with objects containing `name`, `type`, optional `default`
- `return_type`: Return type string (NULL for constructors/void)
- Language-specific formats apply

---

## Capsule: FunctionsModifiers

**Invariant**
`is_static` and `is_async` are boolean flags; `visibility` is the access modifier.

**Example**
```sql
-- Static methods
SELECT qualified_name FROM Functions WHERE is_static = true;

-- Async methods
SELECT name, declaring_type FROM Functions WHERE is_async = true;

-- Public API surface
SELECT name, signature FROM Functions
WHERE visibility = 'public' AND declaring_type IS NOT NULL;

-- Extension methods (static + first param 'this')
SELECT name, signature FROM Functions
WHERE is_static = true AND signature LIKE '%this %';
```

**Depth**
- `is_static`: true/false, extracted from properties
- `is_async`: true/false, extracted from properties
- `visibility`: `public`, `private`, `internal`, `protected`, or NULL
- Defaults to false if property not present

---

## Capsule: FunctionsLocation

**Invariant**
`start_line` gives the function's starting position; use `uri` fragment for precise addressing.

**Example**
```sql
-- Functions with location info
SELECT name, file_name, start_line, end_line FROM Functions WHERE start_line IS NOT NULL;

-- Longest methods by line count
SELECT name, declaring_type, (end_line - start_line) AS lines
FROM Functions WHERE end_line IS NOT NULL ORDER BY lines DESC LIMIT 10;

-- Build URI with line range
SELECT name, file_uri || '#line=' || start_line || ',' || end_line AS func_uri FROM Functions;

-- Preview function with snippet
SELECT name, s.text
FROM Functions f, LATERAL snippet(f.uri, 5) s
WHERE f.name = 'ProcessOrder' AND s.is_focus;
```

**Depth**
- `start_line`: First line of function definition (may be NULL)
- `end_line`: Last line of function definition (may be NULL)
- `uri`: Full URI with fragment for direct addressing
- Use `snippet(uri, context)` to preview function source code
- `span_id` links to full span details in `span` table

---

## Capsule: FunctionsFiltering

**Invariant**
Filter by declaring type, language, visibility, or modifiers.

**Example**
```sql
-- Methods of a specific class
SELECT name, signature FROM Functions WHERE declaring_type = 'OrderProcessor';

-- Constructors only
SELECT qualified_name FROM Functions WHERE function_kind = 'constructor';

-- By language
SELECT name FROM Functions WHERE lang = 'typescript';

-- Exclude test methods
SELECT name FROM Functions
WHERE file_uri NOT LIKE '%test%' AND name NOT LIKE 'Test%';
```

**Depth**
- `declaring_type`: Parent class/type name, NULL for top-level functions
- `function_kind`: `method`, `constructor`, `function`
- `lang`: `csharp`, `typescript`, etc.
- Combine filters for precise queries

---

## Common Patterns

| Goal | Query |
|------|-------|
| All functions | `SELECT * FROM Functions` |
| By kind | `WHERE function_kind = 'method'` |
| By class | `WHERE declaring_type = 'MyClass'` |
| Public only | `WHERE visibility = 'public'` |
| Async methods | `WHERE is_async = true` |
| Static methods | `WHERE is_static = true` |
| Constructors | `WHERE function_kind = 'constructor'` |
| By return type | `WHERE return_type LIKE '%Task%'` |
| Many params | `ORDER BY json_array_length(parameters) DESC` |
| By start line | `ORDER BY start_line` |

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `uri` | string | Full URI with fragment (file + line range) |
| `file_uri` | string | Parent file URI (no fragment) |
| `file_name` | string | Filename only |
| `name` | string | Simple function/method name |
| `qualified_name` | string | Fully qualified name (type.name) |
| `function_kind` | string | `method`, `constructor`, `function` |
| `declaring_type` | string | Parent type name (NULL for top-level) |
| `visibility` | string | `public`, `private`, `internal`, `protected` |
| `signature` | string | Full signature or headline |
| `return_type` | string | Return type (NULL for void/constructors) |
| `parameters` | json | Array of {name, type, default?} |
| `lang` | string | Language (`csharp`, `typescript`, etc.) |
| `is_static` | boolean | Static modifier |
| `is_async` | boolean | Async modifier |
| `start_line` | integer | First line of function (may be NULL) |
| `end_line` | integer | Last line of function (may be NULL) |
| `headline` | string | X-ray one-line summary |
| `structure` | string | X-ray detailed structure |
| `node_id` | uuid | Foreign key to `node` table |
| `span_id` | uuid | Foreign key to `span` table |
