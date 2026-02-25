---
description: "Python format support: file types, extracted structure, classifier behavior, node properties, node kinds, and edge types."
tags: ["python", "format", "indexing", "graph", "annotations"]
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

## Related Views

- `python_types` - type inventory and inheritance metadata
- `python_methods` - member inventory and method semantics
- `python_imports` - import/dependency inventory
- `Functions` - includes `py.member` and `py.function`
- `Types` - includes `py.type`
