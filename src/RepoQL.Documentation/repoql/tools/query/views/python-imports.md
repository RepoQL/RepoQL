---
description: "python_imports(document_uri, specifier, imported_names, is_relative, relative_level, is_type_checking_only, dependency_type)"
tags: ["query", "views", "python", "imports", "dependencies"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Python Imports View

Python import edges (`IMPORTS`) normalized into rows for dependency and type-checking analysis.

## Quick Reference

```sql
-- All Python imports
SELECT document_uri, specifier, imported_names FROM python_imports;

-- Relative imports
SELECT document_uri, specifier, relative_level
FROM python_imports
WHERE is_relative = true;

-- Type-checking-only dependencies
SELECT document_uri, specifier
FROM python_imports
WHERE is_type_checking_only = true;
```

---

## Capsule: PythonImportsFilter

**Invariant**
`python_imports` exposes one row per Python import edge with normalized selector fields.

**Example**
```sql
SELECT document_uri, specifier
FROM python_imports
WHERE specifier LIKE 'django.%';

SELECT document_uri, specifier, imported_names
FROM python_imports
WHERE imported_names IS NOT NULL;
```
//BOUNDARY: This view covers Python documents only (`document.properties->>'language' = 'python'`).

**Depth**
- `specifier` keeps module path as written (`pkg.mod`, `.`, `..core`)
- `imported_names` captures `from ... import ...` bindings (with aliases)
- `is_relative` and `relative_level` preserve explicit relative import semantics
- SeeAlso: raw `edge` rows where `type = 'IMPORTS'`

---

## Capsule: PythonImportsTypeChecking

**Invariant**
Type-checking-only imports are flagged so agents can distinguish runtime and static dependencies.

**Example**
```sql
SELECT document_uri, specifier
FROM python_imports
WHERE is_type_checking_only = true;

SELECT document_uri,
       SUM(CASE WHEN is_type_checking_only THEN 1 ELSE 0 END) AS type_only_count
FROM python_imports
GROUP BY document_uri
ORDER BY type_only_count DESC;
```
//BOUNDARY: Detection is syntactic (`if TYPE_CHECKING` scope). Runtime behavior is not evaluated.

**Depth**
- `is_type_checking_only = true` for imports under type-checking guards
- Useful for dependency cleanup and import-cycle analysis
- Complements but does not replace runtime module tracing
- SeeAlso: `python_methods`, `python_types`

---

## Capsule: PythonImportsDependency

**Invariant**
`dependency_type` provides a coarse dependency bucket derived from import style.

**Example**
```sql
SELECT dependency_type, COUNT(*) AS imports
FROM python_imports
GROUP BY dependency_type;

SELECT document_uri, specifier
FROM python_imports
WHERE dependency_type = 'internal';
```
//BOUNDARY: `dependency_type` is heuristic. Relative imports are classified as `internal`; non-relative imports are `unknown`.

**Depth**
- `internal`: relative imports (`from .` / `from ..`)
- `unknown`: absolute imports without package-resolution context
- For deeper attribution, combine with workspace path conventions
- SeeAlso: `Files`, `Filesystems`

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `document_uri` | string | Importing document URI |
| `specifier` | string | Module specifier text |
| `imported_names` | string | Imported names and aliases summary |
| `is_relative` | boolean | Relative import flag |
| `relative_level` | integer | Number of leading dots for relative imports |
| `is_type_checking_only` | boolean | Import inside `TYPE_CHECKING` guard |
| `dependency_type` | string | Heuristic dependency category |
