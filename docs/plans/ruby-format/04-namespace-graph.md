---
description: Plan for Ruby format — constant extraction, require/require_relative dependency edges, method aliases, and namespace views
tags: [format, ruby, plan, constants, requires, aliases, namespace]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Ruby — Namespace Graph

Implements: [Ruby Format Design](../../designs/current/ruby-format.md) — Graph Materialization (rb.constant nodes, REQUIRES edges, ALIASES edges), SQL Views (ruby_constants, ruby_requires, ruby_aliases)

## Scope

**Covers:**
- `rb.constant` node materialization with name, qualified_name, namespace
- REQUIRES edges from document to required path (require and require_relative)
- ALIASES edges between method names (alias and alias_method)
- SQL views: `ruby_constants`, `ruby_requires`, `ruby_aliases`
- Tests: constant extraction, require paths, alias edge creation

**Does not cover:**
- Circular require detection (extension point — requires multi-file analysis)
- Require path resolution to actual file URIs (multi-file analysis phase)
- Metaprogramming patterns (Plan: 05-metaprogramming)
- Gemspec/Gemfile dependency extraction (extension point)

## Enables

Once this exists:
- **Constants are discoverable** — `SELECT * FROM ruby_constants WHERE namespace = 'MyApp::Config'`
- **Dependency graph is queryable** — `SELECT * FROM ruby_requires WHERE is_internal` shows internal file dependencies
- **External dependencies visible** — `SELECT required_path, COUNT(*) FROM ruby_requires WHERE NOT is_internal GROUP BY 1` shows gem usage
- **Alias resolution** — `SELECT * FROM ruby_aliases WHERE alias_name = 'find_by_name'` shows the original method
- **Future:** Multi-file analysis can resolve require paths to file URIs and detect circular dependencies

## Prerequisites

- Plan 02 complete — document and `rb.type` nodes exist for containment edges and view joins
- `RubyTreeSitterClient` extracts constant assignments, require calls, and alias/alias_method statements (Plan 01 queries)

## North Star

Every constant, every dependency, every alias — queryable. An agent can see what a file requires without reading it, know whether a dependency is internal or external, and trace method aliases back to their source.

## Done Criteria

### Constants
- The materializer shall create `rb.constant` nodes for constant assignments (e.g., `MAX_RETRIES = 3`)
- `rb.constant` nodes shall have props: `name`, `qualified_name` (including enclosing module/class namespace)
- The materializer shall create `HAS_PART` composition edges from the enclosing scope (document, class, or module) to the constant
- When a constant is assigned at the top level, the parent shall be the document node
- When a constant is assigned inside a class/module, the parent shall be the enclosing `rb.type` node

### Requires
- The materializer shall create REQUIRES edges from the document node for `require 'path'` statements
- The materializer shall create REQUIRES edges from the document node for `require_relative 'path'` statements
- REQUIRES edges shall carry `path` (the string argument) and `is_relative` ("true"/"false") in props
- REQUIRES edges shall be reference edges: `IsComposition = false`, `DstId = null`
- When the require argument is not a string literal (e.g., a variable), no edge shall be created

### Surface Model
- `RubyDocumentSurface` shall be extended with `Aliases[]` collection (add `RubyAliasInfo` surface type with new_name, original_name, alias_type, byte range)
- The parser shall populate `Aliases[]` from the tree-sitter client's alias extraction results

### Aliases
- The materializer shall create ALIASES edges for `alias new_name old_name` statements
- The materializer shall create ALIASES edges for `alias_method :new_name, :old_name` calls
- ALIASES edges shall carry `alias_type` ("alias" or "alias_method") in props
- ALIASES edges shall be reference edges with `DstId = null` when the original method node cannot be resolved within the same file
- When both alias source and target are within the same class, the edge shall connect the corresponding `rb.member` nodes

### SQL Views
- `ruby_constants` shall show: file_uri, namespace (enclosing qualified_name), constant_uri, name, qualified_name
- `ruby_requires` shall show: file_uri, required_path, is_internal (boolean), dependency_type ("internal"/"external")
- `ruby_aliases` shall show: source_uri, alias_name, alias_type, original_name, original_uri
- `ruby_aliases` shall use LEFT JOIN on destination so aliases with unresolved targets still appear

## Constraints

- **Require paths are strings, not resolved paths** — `require 'active_record'` stores `"active_record"` as the path. Resolution to actual file URIs is a multi-file analysis concern
- **is_relative classification** — `require_relative` is always internal. `require` with a relative-looking path (starting with `./` or `../`) is still classified as external (it's the method name that matters, not the path shape)
- **Alias edges are best-effort within file** — cross-file alias resolution (aliasing a method from an included module) is deferred to multi-file analysis

## References

- [Ruby Format Design](../../designs/current/ruby-format.md) — Graph Materialization (rb.constant, REQUIRES, ALIASES), SQL Views (ruby_constants, ruby_requires, ruby_aliases)
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Require argument is not a string literal (variable, interpolation) | Skip edge silently — too common to be a diagnostic |
| Constant assignment has a complex left-hand side | Skip node, log diagnostic |
| Alias syntax is malformed | Skip edge, log diagnostic |
