---
description: Plan for Python format — classification, materialization, X-ray summaries, SQL views, DI registration, encoding handling, and shared view integration
tags: [format, python, plan, loader, materialization, views]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Python — Core Format Loader

Implements: [Python Format Design](../../designs/future/python-format.md) — Classification, Visibility, Decorator Semantics, Type Annotation Extraction, Instance Variable Discovery, Docstring Extraction, Import Handling, Graph Materialization, X-Ray Summaries, SQL Views, Project Structure (DI registration)

## Scope

**Covers:**
- `PythonClassifier` — pipeline processor for media type assignment
- `PythonMediaTypes` — media type constants for `.py`, `.pyw`, `.pyi`
- `PythonLoader` — `IFormatLoader`, `IFormatMaterializer`, `IFormatSchemaProvider`
- `PythonParser` — `IAsyncPipeline<IClassifiedArtifact, Records?>`
- `PythonDocumentState` — state transfer between load and materialize
- Materialization: `document`, `py.type`, `py.member`, `py.function` nodes
- Materialization: `HAS_PART`, `EXTENDS`, `IMPORTS` edges
- All node properties: decorator semantic mapping, class kind inference, type annotations, visibility, async/generator, docstrings
- Absorbed attributes: `variables` JSON on `py.type`, `constants` and `type_aliases` JSON on `document`
- Dataclass `__init__` generation (`is_generated: true`, `generator: "dataclass"`)
- Enum members as `constants` JSON entries, NamedTuple fields as `variables` JSON entries
- `__slots__` as property on `py.type`, `__all__` as `all_exports` on `document`
- X-ray headline and structure generation (with docstrings, constants, type aliases, variables)
- `python_views.sql` with `python_types`, `python_methods`, `python_imports` views
- Shared `functions.sql` update: add `py.member`, `py.function`
- PEP 263 encoding handling in `LoadAsync`
- Package init / entry point / stub role detection from filename
- TYPE_CHECKING import detection
- `PythonServiceCollectionExtensions.AddPythonFormat()` and registration in `AddRepoIndexer()`
- Tests: round-trip materialization, class kinds, decorators, types, variables, imports, docstrings, visibility, async, constants, type aliases, encoding

**Does not cover:**
- Metaprogramming honesty annotations (Plan: 03-annotations-documentation)
- Framework pattern annotations — ORM fields (Plan: 03-annotations-documentation)
- `AnnotationSources` registration for re-index cleanup (Plan: 03-annotations-documentation)
- `help://` documentation (Plan: 03-annotations-documentation)

## Enables

Once this exists:
- **Python files are queryable** — `SELECT * FROM python_types WHERE type_kind = 'dataclass'` returns dataclasses across the codebase
- **Shared views work** — Python types appear in the cross-format `Types` view; Python methods and functions appear in `Functions`
- **Explore finds Python** — `explore(keywords="python class user")` returns headlines with class names, member lists, token counts
- **Read shows structure** — `read("file:///app/models.py => structure", 1000)` shows indented outline with docstrings, visibility symbols, variables, constants
- **Symbol navigation works** — `read("file:///app/views.py#symbol=UserViewSet.create")` resolves through node qualified_name matching
- **Import graph traversable** — `SELECT * FROM python_imports WHERE specifier LIKE '%auth%'` finds all modules importing auth
- **Plan 03 can proceed** — annotations attach to nodes that this plan creates

This is the value-delivery increment. After this, agents can work with Python codebases.

## Prerequisites

- Plan 01 complete — `PythonTreeSitterClient` operational and tested
- `PythonTreeSitterClient.Parse()` returns `PythonDocumentSurface` with byte ranges for all structural elements
- All surface model types stable (PythonClassInfo, PythonMethodInfo, etc.)
- `PythonConstants` defined (node kinds, edge types, property keys)

## North Star

Index a Python file. Query its classes, methods, decorators, type annotations, and imports through the same SQL surface as C#, TypeScript, PHP, and Ruby. See the structure — with docstrings, visibility, and variables — without reading the file. The first query an agent tries should work.

## Done Criteria

### Classification
- The classifier shall assign `text/x-python` with kind `code.python` for `.py` files
- The classifier shall assign kind `code.python` for `.pyw` files (Windows GUI Python)
- The classifier shall assign kind `code.python.stub` for `.pyi` files
- The classifier shall assign kind `code.python` for `conftest.py`, `setup.py`, `__init__.py`, `__main__.py`
- When file extension is unrecognized, the classifier shall call `next()`

### Materialization — Document Node
- The materializer shall create one `document` node with `language: "python"`, `line_count`, `byte_size`
- When file is `__init__.py`, the document shall have `role: "package_init"`
- When file is `__main__.py`, the document shall have `role: "entry_point"`
- When file is `.pyi`, the document shall have `role: "stub"`
- When module has a docstring, the document shall have `docstring` property
- When `__all__` is defined, the document shall have `all_exports` property (JSON array of names)
- The document shall have `constants` property — JSON array of `{name, type, is_final, value_preview}` entries
- The document shall have `type_aliases` property — JSON array of `{name, definition}` entries

### Materialization — Type Nodes
- The materializer shall create `py.type` nodes for classes with props: `name`, `qualified_name`, `type_kind`, `extends`, `metaclass`, `namespace`, `decorators` (JSON array), `is_abstract`, `docstring`, `slots`
- The materializer shall create `variables` JSON property on `py.type` — array of `{name, type, variable_kind}` entries combining instance variables and class variables
- The `type_kind` shall be inferred from decorators and base classes per the design's class kind inference table: `dataclass`, `enum`, `namedtuple`, `typeddict`, `protocol`, `abstract`, or `class`
- When `@dataclass` is detected and no explicit `__init__` exists, the materializer shall generate a `py.member` node with `is_generated: true`, `generator: "dataclass"`, parameters derived from the class's annotated variables
- Enum members shall be added to the document's `constants` JSON
- NamedTuple fields shall be added to the type's `variables` JSON

### Materialization — Member Nodes
- The materializer shall create `py.member` nodes for class methods with props: `name`, `qualified_name`, `kind`, `declaring_type`, `accessibility`, `is_static`, `is_classmethod`, `is_async`, `is_generator`, `uses_async_with`, `uses_async_for`, `parameters` (JSON), `return_type`, `decorators` (JSON array), `docstring`
- When method has `@property`, `kind` shall be `"property"`
- When method has `@staticmethod`, `is_static` shall be `true` and `kind` shall stay `"method"`
- When method has `@classmethod`, `is_static` and `is_classmethod` shall be `true` and `kind` shall stay `"method"`
- When method has `@abstractmethod`, `is_abstract` shall be `true`
- When method has `@overload`, `is_overload` shall be `true`
- When method has `@override`, the decorator shall be stored in the `decorators` JSON array (informational only — no semantic effect on node properties)
- The `qualified_name` property on `py.type`, `py.member`, and `py.function` nodes shall use dot-separated nesting (`Outer.Inner.method`) matching the `#symbol=` fragment resolution pattern
- Visibility shall be determined from naming convention: `name` → `public`, `_name` → `private`, `__name` → `private`, `__name__` → `public`

### Materialization — Function Nodes
- The materializer shall create `py.function` nodes for top-level functions with props: `name`, `kind: "function"`, `is_async`, `is_generator`, `uses_async_with`, `uses_async_for`, `parameters` (JSON), `return_type`, `decorators` (JSON array), `docstring`
- Visibility shall follow the same naming convention as methods

### Materialization — Edges
- The materializer shall create `HAS_PART` composition edges from document to types and top-level functions with `ordinal` reflecting source order
- The materializer shall create `HAS_PART` composition edges from types to their members with `ordinal` reflecting source order
- The materializer shall create `EXTENDS` edges from classes to their base classes with `ordinal` tracking MRO position (first base = 0)
- `EXTENDS` edges shall be deferred references (`DstId = null`, target name in props) — same pattern as Ruby, C#, PHP
- The materializer shall create `IMPORTS` edges on the document node with properties: `specifier`, `names`, `is_relative`, `relative_level`, `is_type_checking_only`
- Each node shall have a span with 1-based line numbers and 0-based byte offsets, created via `DocumentModel.LineMap.GetSpan(startByte, endByte)`

### X-Ray Summaries
- The headline shall follow: `{filename} | {primary_declaration} | {key_members} | {line_count} ln, ~{token_count} tok`
- When file has one dominant class, primary_declaration shall be the class signature (e.g., `class UserViewSet(ModelViewSet)`)
- When file has multiple classes, primary_declaration shall summarize (e.g., `3 classes`)
- When file has no classes but functions, primary_declaration shall show function count (e.g., `8 functions`)
- When file has mostly constants, primary_declaration shall show constant count (e.g., `5 constants`)
- When file has type aliases as primary content, primary_declaration shall show them (e.g., `3 type aliases, 2 classes`)
- When file is `__init__.py` with `__all__`, primary_declaration shall show re-exports (e.g., `package | re-exports: User, Profile`)
- When file is `.pyi` stub, primary_declaration shall be prefixed with `stub`
- The structure shall show indented outline with visibility symbols: `+` public, `-` private, `~` instance variable
- The structure shall include `#symbol=` anchors for each method, function, and class
- The structure shall show docstring summary lines (first line, PEP 257) as `#` comments above entities when present
- The structure shall show module-level constants with type and value (e.g., `MAX_RETRIES: Final[int] = 3`)
- The structure shall show type aliases (e.g., `type UserId = int | str`)
- The structure shall show the `async` keyword on async methods
- The structure shall show type annotations inline on parameters and return types

### SQL Views
- `python_types` shall show: `file_uri`, `type_uri`, `name`, `qualified_name`, `type_kind`, `extends`, `metaclass`, `is_abstract`, `decorators`, `docstring`, `slots`, `variables`, `structure`
- `python_methods` shall show: `file_uri`, `type_uri`, `type_name`, `type_qualified_name`, `method_uri`, `headline`, `name`, `method_kind`, `visibility`, `is_static`, `is_classmethod`, `is_async`, `is_generator`, `uses_async_with`, `uses_async_for`, `is_generated`, `is_overload`, `generator`, `parameters`, `return_type`, `decorators`, `docstring`
- `python_imports` shall show: `file_uri`, `specifier`, `imported_names`, `is_relative`, `relative_level`, `is_type_checking_only`, `dependency_type`
- Views shall be embedded as `Schema/python_views.sql` and registered via `IFormatSchemaProvider`

### Shared View Integration
- `functions.sql` shall include `'py.member'` and `'py.function'` in the node kind filter
- The shared `Functions` view shall NOT include `py.member` nodes with `kind: "property"` (properties have different semantics than callable functions — the existing `$.kind` property filter already excludes them)
- `py.type` nodes shall appear in the shared `Types` view via the existing `%.type` pattern match (no change to `types.sql` needed)

### DI Registration
- `AddPythonFormat()` shall register `PythonLoader` as `IFormatSchemaProvider`
- `AddPythonFormat()` shall register a `FormatDescriptor` for `.py`, `.pyw`, `.pyi` extensions
- `AddPythonFormat()` shall register `PythonClassifier` and `PythonParser` as indexing processors
- `AddPythonFormat()` shall be called from `AddRepoIndexer()` in `RepoIndexerServiceCollectionExtensions`

### Encoding Handling
- When reading a Python file, `LoadAsync` shall check the first two lines for a PEP 263 encoding cookie (`# coding=...` or `# -*- coding: ... -*-`)
- When a non-UTF-8 encoding is detected, the file shall be re-read with the correct encoding and converted to UTF-8 before parsing
- When no encoding cookie is found, the file shall be read as UTF-8 (Python 3 default)

### State Transfer
- `PythonDocumentState` shall carry the `PythonDocumentSurface`, digest, size, media type, and store URI
- State shall be set in `DocumentModel.Metadata` during loading and consumed during materialization

## Constraints

- **Follow Ruby pattern** — loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, schema registration via `IFormatSchemaProvider`. Mirror `RubyLoader` structure
- **X-ray built in C#** — no Liquid templates; build headline and structure strings directly, following Ruby convention
- **Property names match shared views exactly** — `name`, `qualified_name`, `kind`, `accessibility`, `extends`, `declaring_type`, `is_static`, `parameters`, `return_type`. Deviation breaks cross-format queries
- **`kind: "method"` for classmethods/staticmethods** — the shared `Functions` view filters on `kind IN ('method', 'constructor', 'function')`. Custom kinds would be silently excluded. Properties (`is_static`, `is_classmethod`) distinguish them
- **No annotations in this increment** — metaprogramming and framework annotations are Plan 03. The surface model captures the hints (MetaprogrammingHints[], FrameworkHints[]) but the materializer does not emit annotations from them yet
- **Cross-format file edit** — `functions.sql` is shared infrastructure; the edit adds two string values to an IN clause

## References

- [Python Format Design](../../designs/future/python-format.md) — full architecture
- [Ruby Format Loader](../../../src/Formats/RepoQL.Formats.Ruby/RubyLoader.cs) — reference implementation for materialization, X-ray building, edge creation
- [Ruby DI Registration](../../../src/Formats/RepoQL.Formats.Ruby/RubyServiceCollectionExtensions.cs) — registration pattern
- [Global Registration](../../../src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs) — where `AddPythonFormat()` is called
- [Shared Functions View](../../../src/RepoQL.Data.DuckDB/Schema/Views/functions.sql) — add `py.member`, `py.function`
- [Shared Types View](../../../src/RepoQL.Data.DuckDB/Schema/Views/types.sql) — validates `%.type` pattern match
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor patterns
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Each extraction phase (classes, functions, imports, decorators, docstrings, variables, constants, type aliases) is independently try/caught. A malformed class definition must never prevent function extraction elsewhere in the file.

| Failure | Behavior |
|---------|----------|
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| PEP 263 encoding detection fails | Fall through to UTF-8; let tree-sitter handle with error nodes |
| Tree-sitter returns ERROR nodes | Skip error regions, extract surrounding structure |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Decorator name unresolvable | Store raw text, skip semantic mapping |
| `__init__` body too complex to walk | Extract what's accessible, skip rest |
| Type annotation syntax not recognized | Store raw text in property |
| Qualified name computation fails (deeply nested) | Use simple name as qualified_name |
