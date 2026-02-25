---
description: Plan for Python format — metaprogramming honesty annotations, framework pattern annotations, annotation cleanup on re-index, and help:// documentation
tags: [format, python, plan, annotations, documentation, metaprogramming]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Python — Annotations + Documentation

Implements: [Python Format Design](../../designs/future/python-format.md) — Metaprogramming (Honest Boundaries), Framework Patterns, Cross-Cutting Concerns (Metaprogramming honesty)

## Scope

**Covers:**
- Metaprogramming honesty annotations emitted during materialization
- Framework pattern annotations emitted during materialization
- `AnnotationSources` registration for annotation cleanup on re-index
- `help://` documentation: Python format overview, view reference pages, shared view doc updates

**Does not cover:**
- Structured docstring parsing — Google/NumPy/Sphinx (design extension point)
- Framework-specific convenience views — `django_models`, `flask_routes` (design extension point)
- `.pyi` stub linking to implementation modules (design extension point)
- Circular import detection (design extension point)
- Decorator argument parsing (design extension point)

## Enables

Once this exists:
- **Honest graph** — agents query `SELECT * FROM annotation WHERE kind = 'python.metaprogramming'` to understand what the graph couldn't capture. When `__getattr__`, `exec`, or metaclasses are present, the agent knows
- **Framework discovery** — agents query `SELECT * FROM annotation WHERE kind = 'python.framework'` to find Django model fields, SQLAlchemy columns, and Pydantic field declarations
- **Self-documenting** — `explore(uriGlob="help://**", keywords="python")` discovers Python format documentation. Agents learn Python-specific views and query patterns through the same tool surface they use on code
- **Re-index correctness** — stale annotations are cleaned up when files are re-indexed

This is the polish increment. After this, the Python format is honest about its boundaries and discoverable through `help://`.

## Prerequisites

- Plan 02 complete — `PythonLoader` materializes nodes, edges, and artifacts
- Surface model delivers `MetaprogrammingHints[]` and `FrameworkHints[]` from Plan 01
- `PythonConstants` defines annotation kinds: `python.metaprogramming`, `python.framework`

## North Star

An agent should know what the graph captured and what it couldn't. When Python's dynamism makes structure invisible, the graph says so — honestly, queryably, and with enough context to decide whether to read the file. An agent should find Python format documentation through the same tools it uses on code.

## Done Criteria

### Metaprogramming Annotations
- When a class defines `__getattr__`, the materializer shall emit a `python.metaprogramming` annotation with message: "dynamic attribute access, graph may be incomplete"
- When `exec(...)` is detected, the materializer shall emit a `python.metaprogramming` annotation with message: "dynamic code execution detected"
- When `eval(...)` is detected, the materializer shall emit a `python.metaprogramming` annotation with message: "dynamic code execution detected"
- When `type()` is called with 3 arguments, the materializer shall emit a `python.metaprogramming` annotation with message: "dynamic class creation"
- When `setattr(...)` is detected, the materializer shall emit a `python.metaprogramming` annotation with message: "dynamic attribute creation"
- When a metaclass defines `__new__` or `__init_subclass__`, the materializer shall emit a `python.metaprogramming` annotation with message: "metaclass may generate members"
- When a module-level attribute assignment targets a `type()` call or uses a function/lambda value, the materializer shall emit a `python.metaprogramming` annotation with message: "possible monkey patch"
- Each annotation shall have a span pointing to the source location of the detected pattern
- Each annotation shall have `scope_document_id` set to the document node

### Framework Pattern Annotations
- When a class-level assignment calls `models.CharField`, `models.IntegerField`, or other `models.*` patterns, the materializer shall emit a `python.framework` annotation with `rule_id: "django_field"` and `message` containing the call expression text
- When a class-level assignment calls `db.Column(...)`, the materializer shall emit a `python.framework` annotation with `rule_id: "sqlalchemy_column"`
- When a class-level assignment calls `Field(...)` (Pydantic), the materializer shall emit a `python.framework` annotation with `rule_id: "pydantic_field"`
- Framework annotations shall have `severity: "info"` and `confidence: "medium"`
- Each annotation shall have a span pointing to the assignment location

### Annotation Cleanup
- `PythonLoader` shall register annotation sources: `"python.metaprogramming"` and `"python.framework"`
- When a file is re-indexed, stale annotations from previous indexing shall be cleaned up via the `AnnotationSources` mechanism
- The `Records` returned from materialization shall include `AnnotationSources` so the pipeline knows which annotation kinds to clean

### help:// Documentation

#### New Pages
- A Python format overview page shall exist at `help:///repoql/formats/python.md` describing: what file types are indexed, what structure is extracted, what the classifier recognizes, what properties are available on each node kind
- A `python_types` view reference page shall exist at `help:///repoql/tools/query/views/python-types.md` with: columns, example queries, capsule documentation
- A `python_methods` view reference page shall exist at `help:///repoql/tools/query/views/python-methods.md` with: columns, example queries, capsule documentation
- A `python_imports` view reference page shall exist at `help:///repoql/tools/query/views/python-imports.md` with: columns, example queries, capsule documentation

#### Updates to Existing Pages
- `help:///repoql/tools/query/views/functions.md` shall be updated to list `py.member` and `py.function` in the Filters depth note
- `help:///repoql/tools/query/views/types.md` shall be updated to mention Python types if it lists format-specific node kinds
- Documentation pages shall follow the existing capsule format (Invariant, Example, Boundary, Depth, SeeAlso)

## Constraints

- **Annotations only** — this plan emits annotations on the annotation table. No new node kinds, no new edge types, no changes to existing node properties
- **Surface model already captures hints** — `MetaprogrammingHints[]` and `FrameworkHints[]` are populated by Plan 01's parser. This plan's materializer reads them and emits annotations. No parser changes needed
- **Documentation follows existing format** — view reference pages follow the capsule pattern established by `functions.md`, `types.md`, `files.md`
- **Detection is syntactic, not semantic** — ORM field patterns match dotted names (`models.*`, `db.Column`, `Field`). False positives are possible; confidence is `medium`. This is documented in the design's trade-offs

## References

- [Python Format Design](../../designs/future/python-format.md) — Metaprogramming (Honest Boundaries), Framework Patterns sections
- [Ruby Metaprogramming](../../../src/Formats/RepoQL.Formats.Ruby/RubyLoader.cs) — reference for how metaprogramming annotations are emitted (search for `ruby.metaprogramming`)
- [Functions View Docs](../../../src/RepoQL.Documentation/repoql/tools/query/views/functions.md) — shared view doc needing update
- [Types View Docs](../../../src/RepoQL.Documentation/repoql/tools/query/views/types.md) — shared view doc needing update
- [Annotations View Docs](../../../src/RepoQL.Documentation/repoql/tools/query/views/annotations.md) — reference for annotation documentation pattern
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Annotation emission failures are non-fatal. If a metaprogramming or framework hint cannot be converted to an annotation (e.g., span creation fails), log a warning and continue. The graph is better without one annotation than without the entire file.

| Failure | Behavior |
|---------|----------|
| Metaprogramming hint has invalid byte range | Log warning, skip annotation, continue |
| Framework hint pattern unrecognized | Skip (should not happen — parser already validated) |
| Annotation write fails | Log warning, continue with remaining annotations |
| help:// doc page has wrong frontmatter | Build warning at snapshot time — caught by CI |
