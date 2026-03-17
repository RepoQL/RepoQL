---
description: "TypeScript — components, declarations, imports, members — typescript_components, typescript_declarations, typescript_imports, typescript_members views"
tags: ["typescript", "javascript", "format", "indexing", "components", "imports", "declarations", "members", "react"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Formats[100%]"]
---

# TypeScript Format

Query TypeScript and JavaScript classes, interfaces, functions, variables, enums, imports, exports, React components, and type members with SQL views. Parsing via Node.js TypeScript compiler API -- Node.js must be installed.

## Indexed File Types

| Extension | Media Type Kind |
|-----------|-----------------|
| `.ts` | `code.typescript` |
| `.tsx` | `code.typescript.react` |
| `.js` | `code.javascript` |
| `.jsx` | `code.javascript.react` |

## Extracted Structure

TypeScript materialization extracts:
- Classes, interfaces, type aliases, and enums
- Top-level functions and exported variables
- Namespace declarations
- Imports (named, default, namespace, side-effect, mixed; value and type imports)
- Exports with export kind tracking
- React component detection (functions returning JSX)
- React hook usage within components
- Class/interface members (methods, fields, constructors, getters, setters, enum members)
- Type parameters, parameter types, return types
- Inheritance (`extends`) and interface implementation (`implements`)

## Node Kinds

- `document` -- TypeScript/JavaScript file document node
- `typescript.type` -- Class, interface, type alias, or enum declaration
- `typescript.function` -- Top-level function declaration
- `typescript.member` -- Method, field, constructor, getter, setter, or enum member on a type
- `ts_decl_variable` -- Top-level variable declaration
- `ts_decl_namespace` -- Namespace declaration

## Edge Types

- `HAS_PART` -- Composition (document -> declarations, type -> members)
- `EXTENDS` -- Base class or extended interface (unresolved, target name in `properties->>'target'`)
- `IMPLEMENTS` -- Interface implementation (unresolved, target name in `properties->>'target'`)

## Node Properties

### `document`

- `media_type` -- Full media type string (e.g. `text/plain;kind=code.typescript`)
- `script_kind` -- Parser script kind
- `imports` -- JSON array of import objects (`specifier`, `kind`, `style`)

### `typescript.type`

- `name`, `qualified_name`, `kind` -- Identity and declaration kind (`class`, `interface`, `type`, `enum`)
- `namespace` -- Always empty string (TypeScript uses modules, not namespaces)
- `accessibility` -- `export` or `internal`
- `signature` -- Headline signature
- `extends` -- Base type name (when present)
- `implements` -- JSON array of implemented interface names

### `typescript.function`

- `name`, `kind` (`function`), `decl_kind` (`function`)
- `accessibility` -- `export` or `internal`
- `signature` -- Full function signature
- `is_exported`, `export_kind` -- Export metadata
- `return_type`, `parameters` -- Type information
- `is_component` -- `true` when detected as a React component
- `hooks` -- JSON array of React hooks used (when `is_component` is true)

### `typescript.member`

- `name`, `kind` -- Member name and kind (`method`, `field`, `constructor`, `getter`, `setter`, `enumMember`)
- `declaring_type` -- Parent type name
- `return_type` -- Return type annotation (methods, getters)
- `type` -- Type annotation (fields)
- `parameters` -- Parameter signature string (methods, constructors, setters)

### `ts_decl_variable`

- `name`, `kind` (`variable`), `decl_kind` (`variable`)
- `is_exported`, `export_kind`
- `is_component` -- `true` when detected as a React component

---

## Views

### typescript_declarations

All top-level declarations in TypeScript/JavaScript files: classes, interfaces, type aliases, enums, functions, variables, and namespaces.

#### Quick Reference

```sql
-- All declarations
SELECT file_uri, name, decl_kind, is_exported FROM typescript_declarations;

-- Exported interfaces
SELECT name, file_uri
FROM typescript_declarations
WHERE decl_kind = 'interface' AND is_exported = true;

-- All classes with their structure
SELECT name, structure
FROM typescript_declarations
WHERE decl_kind = 'class';

-- Declaration kind breakdown per file
SELECT file_uri, decl_kind, COUNT(*) AS cnt
FROM typescript_declarations
GROUP BY file_uri, decl_kind
ORDER BY cnt DESC;
```

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Parent document URI |
| `declaration_uri` | string | Declaration symbol URI |
| `headline` | string | X-ray headline (signature with export/component markers) |
| `name` | string | Declaration name |
| `decl_kind` | string | Declaration kind: `class`, `interface`, `type`, `enum`, `function`, `variable`, `namespace` |
| `is_exported` | boolean | Whether the declaration is exported |
| `export_kind` | string | Export style: `named`, `default`, or null |
| `structure` | string | X-ray structure text (signature + members) |

---

### typescript_components

React components -- a filtered subset of `typescript_declarations` where `is_component = true`. Components are functions or variables that return JSX.

#### Quick Reference

```sql
-- All React components
SELECT file_uri, name, headline FROM typescript_components;

-- Components per file
SELECT file_uri, COUNT(*) AS component_count
FROM typescript_components
GROUP BY file_uri
ORDER BY component_count DESC;

-- Component signatures
SELECT name, headline
FROM typescript_components
WHERE decl_kind = 'function';
```

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Parent document URI |
| `component_uri` | string | Component symbol URI |
| `headline` | string | X-ray headline (includes `(component)` marker) |
| `name` | string | Component name |
| `decl_kind` | string | Declaration kind (typically `function`) |
| `structure` | string | X-ray structure text |

---

### typescript_imports

Import statements extracted from TypeScript and JavaScript documents. One row per import specifier, with kind and style classification.

#### Quick Reference

```sql
-- All imports
SELECT file_uri, specifier, import_kind, import_style FROM typescript_imports;

-- Package (non-relative) dependencies
SELECT specifier, COUNT(*) AS usage_count
FROM typescript_imports
WHERE source_kind = 'package'
GROUP BY specifier
ORDER BY usage_count DESC;

-- Relative imports (internal modules)
SELECT file_uri, specifier
FROM typescript_imports
WHERE source_kind = 'relative';

-- Type-only imports
SELECT file_uri, specifier
FROM typescript_imports
WHERE import_kind = 'type';

-- Side-effect imports (e.g. CSS, polyfills)
SELECT file_uri, specifier
FROM typescript_imports
WHERE import_style = 'sideEffect';
```

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Importing document URI |
| `specifier` | string | Import module specifier (e.g. `react`, `../utils/helpers`) |
| `import_kind` | string | `value` or `type` |
| `import_style` | string | `named`, `default`, `namespace`, `sideEffect`, or `mixed` |
| `source_kind` | string | `relative` (starts with `.`) or `package` |

---

### typescript_members

Members of TypeScript types (classes, interfaces, enums). Joins through type -> member composition edges.

#### Quick Reference

```sql
-- All members
SELECT file_uri, type_name, member_name, member_kind FROM typescript_members;

-- Methods with return types
SELECT type_name, member_name, return_type, parameters
FROM typescript_members
WHERE member_kind = 'method' AND return_type IS NOT NULL;

-- Fields with type annotations
SELECT type_name, member_name, type
FROM typescript_members
WHERE member_kind = 'field';

-- Enum values
SELECT type_name, member_name
FROM typescript_members
WHERE member_kind = 'enumMember';

-- Constructors
SELECT type_name, parameters
FROM typescript_members
WHERE member_kind = 'constructor';
```

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Parent document URI |
| `type_uri` | string | Parent type symbol URI |
| `type_name` | string | Parent type name |
| `member_uri` | string | Member symbol URI |
| `member_name` | string | Member name |
| `member_kind` | string | `method`, `field`, `constructor`, `getter`, `setter`, or `enumMember` |
| `return_type` | string | Return type annotation (methods, getters) |
| `type` | string | Type annotation (fields) |
| `parameters` | string | Parameter signature string |

---

## Common Patterns

```sql
-- Find all exported types and their members
SELECT d.name AS type_name, m.member_name, m.member_kind
FROM typescript_declarations d
JOIN typescript_members m ON d.file_uri = m.file_uri AND d.name = m.type_name
WHERE d.is_exported = true AND d.decl_kind IN ('class', 'interface');

-- Find classes that extend a base class
SELECT n.uri, json_extract_string(n.properties, '$.name') AS name,
       json_extract_string(e.properties, '$.target') AS base_class
FROM node n
JOIN edge e ON e.source_node_id = n.id AND e.type = 'EXTENDS'
WHERE n.kind = 'typescript.type';

-- Find interface implementations
SELECT json_extract_string(n.properties, '$.name') AS type_name,
       json_extract_string(e.properties, '$.target') AS interface_name
FROM node n
JOIN edge e ON e.source_node_id = n.id AND e.type = 'IMPLEMENTS'
WHERE n.kind = 'typescript.type';

-- Dependency graph: which packages does each file use?
SELECT file_uri, LIST(DISTINCT specifier) AS packages
FROM typescript_imports
WHERE source_kind = 'package'
GROUP BY file_uri;

-- Find React components and their hooks
SELECT json_extract_string(n.properties, '$.name') AS component,
       json_extract_string(n.properties, '$.hooks') AS hooks
FROM node n
WHERE n.kind IN ('typescript.function', 'ts_decl_variable')
  AND json_extract_string(n.properties, '$.is_component') = 'true';

-- Search -> structure -> snippet
WITH hits AS (
  SELECT uri FROM search('authentication middleware', k := 5)
)
SELECT h.uri, d.name, d.decl_kind, d.headline
FROM hits h
JOIN typescript_declarations d ON d.file_uri = h.uri
WHERE d.is_exported = true
ORDER BY h.uri, d.name;

-- X-ray: understand file contents without reading
SELECT headline, summary, structure
FROM artifact a
JOIN node n ON n.artifact_id = a.id
WHERE n.uri = 'file:///src/services/auth.ts';

-- Quick inventory
SELECT * FROM Files WHERE lang LIKE '%typescript%' OR lang LIKE '%javascript%';
```

## URIs

```sql
-- Symbol-based URIs
-- file:///src/services/auth.ts#symbol=AuthService&line=10,50
-- file:///src/services/auth.ts#symbol=AuthService.validate&line=20,35

-- Resolve location
SELECT * FROM entities_by_uri('file:///src/services/auth.ts#symbol=AuthService');

-- Preview
SELECT line_number, text
FROM snippet('file:///src/services/auth.ts#line=20', 5);
```

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Querying `typescript_members` for top-level functions | Top-level functions are in `typescript_declarations` with `decl_kind = 'function'`, not in `typescript_members` |
| Expecting resolved `EXTENDS`/`IMPLEMENTS` edge destinations | These edges are unresolved -- `destination_node_id` is null. Use `properties->>'target'` for the target name |
| Looking for `namespace` in type properties | TypeScript uses modules, not namespaces. The `namespace` property on types is always empty |
| Filtering components from `typescript_declarations` | Use `typescript_components` instead -- it filters to `is_component = true` automatically |
| Using `import_kind` to find relative imports | `import_kind` is `value` vs `type`. Use `source_kind = 'relative'` for relative imports |
| Expecting member `parameters` as JSON array | `parameters` is a formatted signature string like `(name: string, age?: number)`, not a JSON array |
