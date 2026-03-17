---
description: "Python — classes, functions, decorators, imports, generators — python_types, python_methods, python_imports views with query patterns"
tags: ["python", "format", "indexing", "types", "methods", "imports"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Formats[100%]"]
---

# Python Format

RepoQL indexes Python source with syntactic extraction and exposes results as graph nodes, edges, annotations, and SQL views.

## Indexed File Types

| Extension | Media Type Kind |
|-----------|-----------------|
| `.py` | `code.python` |
| `.pyw` | `code.python` |
| `.pyi` | `code.python.stub` |

## Extracted Structure

Python materialization extracts:
- Classes and nested classes
- Methods and top-level functions
- Imports (absolute, relative, type-checking-only)
- Decorators and semantic method/type kinds
- Type annotations (parameters, returns, variables, aliases)
- Class and instance variables
- Module/class/method docstrings
- Constants and `__all__` exports
- Metaprogramming and framework honesty annotations

## Classifier Recognition

The classifier resolves Python support from filename/extension patterns:
- `.py`, `.pyw` as standard Python modules
- `.pyi` as Python stub modules

When a file is recognized, materialization writes Python graph records and Python SQL views become queryable (`python_types`, `python_methods`, `python_imports`).

## Node Kinds

- `document` - Python file document node
- `py.type` - Class/type declarations
- `py.member` - Methods/properties on a class
- `py.function` - Top-level functions

## Edge Types

- `HAS_PART` - Composition (document -> types/functions, type -> members)
- `EXTENDS` - Type inheritance relationships
- `IMPORTS` - Import dependencies

## Node Properties

### `document`

Common properties:
- `language`, `line_count`, `byte_size`
- `constants`, `type_aliases`
- `docstring` (when module docstring exists)
- `all_exports` (`__all__` values when present)
- `role` (`package_init`, `entry_point`, `stub`)

### `py.type`

Common properties:
- `name`, `qualified_name`, `type_kind`
- `extends`, `metaclass`, `namespace`
- `decorators`, `is_abstract`
- `variables`, `slots`
- `docstring`

### `py.member`

Common properties:
- `name`, `qualified_name`, `declaring_type`
- `kind`, `accessibility`
- `is_static`, `is_classmethod`, `is_async`, `is_generator`
- `uses_async_with`, `uses_async_for`
- `parameters`, `return_type`, `decorators`, `docstring`
- `is_generated`, `generator`, `is_overload` (when applicable)

### `py.function`

Common properties:
- `name`, `qualified_name`, `kind`
- `accessibility`
- `is_async`, `is_generator`, `uses_async_with`, `uses_async_for`
- `parameters`, `return_type`, `decorators`, `docstring`

## Honesty Annotations

Python emits informational annotations to mark graph boundaries and framework patterns:
- `python.metaprogramming` from dynamic constructs (`exec`, `eval`, `__getattr__`, PEP 562 module-level `__getattr__`/`__dir__`, `importlib.import_module`, metaclass hooks, etc.)
- `python.framework` from common field patterns (`models.*`, `db.Column`, `Field(...)`)

Source: `repoql.formats.python`

---

## Views

### python_types

Type-level Python declarations (`py.type`) with inheritance, decorators, and variable summaries.

#### Quick Reference

```sql
-- All Python types
SELECT qualified_name, type_kind, file_uri FROM python_types;

-- Dataclasses and protocols
SELECT qualified_name, type_kind
FROM python_types
WHERE type_kind IN ('dataclass', 'protocol');

-- Types with typed variables
SELECT qualified_name, variables
FROM python_types
WHERE json_array_length(variables) > 0;
```

#### Capsule: PythonTypesFiltering

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

#### Capsule: PythonTypesInheritance

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

#### Capsule: PythonTypesVariables

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

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Parent Python document URI |
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

---

### python_methods

Method-level Python members (`py.member`) with semantic kind, async flags, decorators, and generated-member metadata.

#### Quick Reference

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

#### Capsule: PythonMethodsKind

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

#### Capsule: PythonMethodsAsync

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

#### Capsule: PythonMethodsGenerated

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

#### Column Reference

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

---

### python_imports

Python import edges (`IMPORTS`) normalized into rows for dependency and type-checking analysis.

#### Quick Reference

```sql
-- All Python imports
SELECT file_uri, specifier, imported_names FROM python_imports;

-- Relative imports
SELECT file_uri, specifier, relative_level
FROM python_imports
WHERE is_relative = true;

-- Type-checking-only dependencies
SELECT file_uri, specifier
FROM python_imports
WHERE is_type_checking_only = true;
```

#### Capsule: PythonImportsFilter

**Invariant**
`python_imports` exposes one row per Python import edge with normalized selector fields.

**Example**
```sql
SELECT file_uri, specifier
FROM python_imports
WHERE specifier LIKE 'django.%';

SELECT file_uri, specifier, imported_names
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

#### Capsule: PythonImportsTypeChecking

**Invariant**
Type-checking-only imports are flagged so agents can distinguish runtime and static dependencies.

**Example**
```sql
SELECT file_uri, specifier
FROM python_imports
WHERE is_type_checking_only = true;

SELECT file_uri,
       SUM(CASE WHEN is_type_checking_only THEN 1 ELSE 0 END) AS type_only_count
FROM python_imports
GROUP BY file_uri
ORDER BY type_only_count DESC;
```
//BOUNDARY: Detection is syntactic (`if TYPE_CHECKING` scope). Runtime behavior is not evaluated.

**Depth**
- `is_type_checking_only = true` for imports under type-checking guards
- Useful for dependency cleanup and import-cycle analysis
- Complements but does not replace runtime module tracing
- SeeAlso: `python_methods`, `python_types`

---

#### Capsule: PythonImportsDependency

**Invariant**
`dependency_type` provides a coarse dependency bucket derived from import style.

**Example**
```sql
SELECT dependency_type, COUNT(*) AS imports
FROM python_imports
GROUP BY dependency_type;

SELECT file_uri, specifier
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

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Importing document URI |
| `specifier` | string | Module specifier text |
| `imported_names` | string | Imported names and aliases summary |
| `is_relative` | boolean | Relative import flag |
| `relative_level` | integer | Number of leading dots for relative imports |
| `is_type_checking_only` | boolean | Import inside `TYPE_CHECKING` guard |
| `dependency_type` | string | Heuristic dependency category |
