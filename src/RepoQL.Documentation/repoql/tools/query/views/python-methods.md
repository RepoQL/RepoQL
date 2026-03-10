---
description: "python_methods(file_uri, type_uri, type_name, type_qualified_name, method_uri, headline, name, method_kind, visibility, is_static, is_classmethod, is_async, is_generator, uses_async_with, uses_async_for, is_generated, is_overload, generator, parameters, return_type, decorators, docstring)"
tags: ["query", "views", "python", "methods", "functions"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Python Methods View

Method-level Python members (`py.member`) with semantic kind, async flags, decorators, and generated-member metadata.

## Quick Reference

```sql
-- All Python methods
SELECT type_qualified_name, name, method_kind FROM python_methods;

-- Async methods
SELECT type_qualified_name, name
FROM python_methods
WHERE is_async = true;

-- Generated methods (for example dataclass __init__)
SELECT type_qualified_name, name, generator
FROM python_methods
WHERE is_generated = true;
```

---

## Capsule: PythonMethodsKind

**Invariant**
`python_methods` includes only members attached to Python types and exposes their method semantics through `method_kind`.

**Example**
```sql
SELECT type_qualified_name, name
FROM python_methods
WHERE method_kind = 'property';

SELECT type_qualified_name, name, visibility
FROM python_methods
WHERE visibility = 'private';
```
//BOUNDARY: Top-level functions are not in this view. Use `Functions` and filter `kind = 'py.function'`.

**Depth**
- `method_kind` reflects semantic classification (`method` or `property`)
- `visibility` follows Python naming conventions (`public`/`private`)
- `decorators` and `parameters` are JSON text payloads
- SeeAlso: `Functions`, `python_types`

---

## Capsule: PythonMethodsAsync

**Invariant**
Async and generator behavior is represented with dedicated boolean flags.

**Example**
```sql
SELECT type_qualified_name, name
FROM python_methods
WHERE is_async = true AND is_generator = true;

SELECT type_qualified_name, name
FROM python_methods
WHERE uses_async_with = true OR uses_async_for = true;
```
//BOUNDARY: Flags are syntactic and statement-based. They do not model awaited call graphs or runtime control flow.

**Depth**
- `is_async`: method defined with `async def`
- `is_generator`: method contains `yield` / `yield from`
- `uses_async_with` and `uses_async_for`: direct usage markers
- SeeAlso: shared `Functions` view (`is_async`)

---

## Capsule: PythonMethodsGenerated

**Invariant**
Generated members are explicitly marked to preserve metaprogramming honesty and avoid confusion with authored code.

**Example**
```sql
SELECT type_qualified_name, name, is_generated, generator
FROM python_methods
WHERE is_generated = true;

SELECT type_qualified_name, name
FROM python_methods
WHERE is_overload = true;
```
//BOUNDARY: Generation markers are based on recognized patterns (for example dataclass). They are not exhaustive runtime metaprogramming coverage.

**Depth**
- `is_generated = true` marks synthesized methods
- `generator` identifies the source pattern (for example `dataclass`)
- `is_overload` marks overload-decorated members
- SeeAlso: `Annotations` with `kind = 'python.metaprogramming'`

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Parent document URI |
| `type_uri` | string | Parent type URI |
| `type_name` | string | Parent type simple name |
| `type_qualified_name` | string | Parent type qualified name |
| `method_uri` | string | Method symbol URI |
| `headline` | string | X-ray headline for the method node |
| `name` | string | Method name |
| `method_kind` | string | Semantic kind (`method` or `property`) |
| `visibility` | string | Naming-convention visibility |
| `is_static` | boolean | Static-like method flag |
| `is_classmethod` | boolean | Classmethod decorator flag |
| `is_async` | boolean | Async method flag |
| `is_generator` | boolean | Generator method flag |
| `uses_async_with` | boolean | Contains `async with` |
| `uses_async_for` | boolean | Contains `async for` |
| `is_generated` | boolean | Generated member flag |
| `is_overload` | boolean | Overload decorator flag |
| `generator` | string | Generator source identifier |
| `parameters` | string | JSON text of parameter definitions |
| `return_type` | string | Return type annotation text |
| `decorators` | string | JSON text of decorators |
| `docstring` | string | Method docstring |
