---
description: "python_types(document_uri, type_uri, name, qualified_name, type_kind, extends, metaclass, is_abstract, decorators, docstring, slots, variables, structure)"
tags: ["query", "views", "python", "types", "classes"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Python Types View

Type-level Python declarations (`py.type`) with inheritance, decorators, and variable summaries.

## Quick Reference

```sql
-- All Python types
SELECT qualified_name, type_kind, document_uri FROM python_types;

-- Dataclasses and protocols
SELECT qualified_name, type_kind
FROM python_types
WHERE type_kind IN ('dataclass', 'protocol');

-- Types with typed variables
SELECT qualified_name, variables
FROM python_types
WHERE json_array_length(variables) > 0;
```

---

## Capsule: PythonTypesFiltering

**Invariant**
`python_types` exposes one row per Python type node and supports filtering by semantic kind and decorators.

**Example**
```sql
SELECT qualified_name, type_kind
FROM python_types
WHERE type_kind = 'enum';

SELECT qualified_name, decorators
FROM python_types
WHERE decorators::text LIKE '%dataclass%';
```
//BOUNDARY: This view includes only `py.type` nodes. Methods/functions are in `python_methods` and `Functions`.

**Depth**
- `type_kind` includes `class`, `dataclass`, `enum`, `protocol`, `abstract`, `typeddict`, `namedtuple`
- `decorators` is stored as JSON text from node properties
- `qualified_name` is the primary identity for class/module nesting
- SeeAlso: `python_methods`, `Types`

---

## Capsule: PythonTypesInheritance

**Invariant**
Inheritance metadata is surfaced through `extends`, `metaclass`, and `is_abstract`.

**Example**
```sql
SELECT qualified_name, extends, metaclass
FROM python_types
WHERE extends IS NOT NULL OR metaclass IS NOT NULL;

SELECT qualified_name
FROM python_types
WHERE is_abstract = true;
```
//BOUNDARY: `extends` is a string summary from properties, not a normalized array. Use raw edges for fine-grained inheritance graphs.

**Depth**
- `extends` is a comma-delimited base-class summary
- `metaclass` is present when `metaclass=...` is declared
- `is_abstract` combines base class and decorator semantics
- SeeAlso: `python_methods`, raw `edge` rows with `type = 'EXTENDS'`

---

## Capsule: PythonTypesVariables

**Invariant**
`variables` provides class + instance variables as a JSON array with name/type/kind.

**Example**
```sql
SELECT qualified_name, variables
FROM python_types
WHERE json_array_length(variables) > 0;

SELECT qualified_name
FROM python_types
WHERE variables::text LIKE '%\"variable_kind\":\"instance\"%';
```
//BOUNDARY: Variable entries are syntactic and scoped to extracted class bodies. Runtime-added attributes are not represented.

**Depth**
- JSON entry shape: `{ "name": "...", "type": "...", "variable_kind": "class|instance" }`
- Ordered by source position
- `slots` is separate and reports `__slots__` declarations
- SeeAlso: `python_methods`, `Annotations` (`python.metaprogramming`)

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `document_uri` | string | Parent Python document URI |
| `type_uri` | string | Type symbol URI |
| `name` | string | Simple type name |
| `qualified_name` | string | Qualified type name |
| `type_kind` | string | Semantic kind (`class`, `dataclass`, etc.) |
| `extends` | string | Base-class summary |
| `metaclass` | string | Declared metaclass, if present |
| `is_abstract` | boolean | Abstract classification flag |
| `decorators` | string | JSON text of decorators |
| `docstring` | string | Type docstring |
| `slots` | string | `__slots__` expression text |
| `variables` | json | Variable summary array |
| `structure` | string | X-ray structure text |
