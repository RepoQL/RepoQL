---
description: Plan for Go format — go.mod and go.work parsing, dependency edges, replace annotations, and module metadata views
tags: [format, go, golang, plan, gomod, dependencies, modules]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Go — Module Metadata

Implements: [Go Format Design](../../designs/future/go-format.md) — go.mod / go.work Parsing, SQL Views (go_dependencies, go_replaces)

## Scope

**Covers:**
- `GoModParser` — line-scanning parser for `go.mod` and `go.work` files
- `GoModInfo` — surface model for module metadata
- Materialization of go.mod/go.work into document nodes, `DEPENDS_ON` edges, and annotations
- Integration with `GoLoader` for `code.go.mod` and `code.go.work` media types
- SQL views: `go_dependencies`, `go_replaces`
- Tests: go.mod parsing, go.work parsing, edge cases

**Does not cover:**
- Import reclassification using module path (extension point — multi-file analysis could use `go.mod` module path to reclassify `IMPORTS` edges as internal/external more accurately)
- Dependency version resolution or update detection
- `go.sum` parsing (extension point)

## Enables

Once this exists:
- **Dependencies queryable** — `SELECT * FROM go_dependencies WHERE NOT is_indirect` shows direct dependencies
- **Indirect dependencies visible** — `SELECT * FROM go_dependencies WHERE is_indirect` shows transitive dependencies
- **Replace directives exposed** — `SELECT * FROM go_replaces` shows local overrides and forks
- **Module identity known** — document node `module_path` property enables internal import reclassification in future multi-file analysis
- **Go version visible** — `SELECT json_extract_string(n.properties, '$.go_version') FROM node n WHERE n.kind = 'document' AND json_extract_string(n.properties, '$.language') = 'go.mod'`

## Prerequisites

- Plan 02 complete — `GoLoader` operational, classification routes `code.go.mod` and `code.go.work` to the loader

## North Star

An agent reads the dependency graph of a Go project from SQL — direct deps, indirect deps, replacements, Go version — without opening `go.mod`. When a module is replaced with a local path, the agent sees it. When an indirect dependency appears in 30 modules, the agent counts it.

## Done Criteria

### GoModParser
- The parser shall accept the text content of a `go.mod` or `go.work` file and return a `GoModInfo`
- The parser shall use line-scanning with a state machine, not tree-sitter
- The parser shall handle both single-line and block (`( ... )`) syntax for `require`, `replace`, `retract`, and `exclude` directives

### go.mod Parsing
- The parser shall extract the `module` path (e.g., `github.com/myorg/myapp`)
- The parser shall extract the `go` version directive (e.g., `1.22`)
- The parser shall extract the `toolchain` directive if present (e.g., `go1.22.0`)
- The parser shall extract `require` directives with module path and version
- The parser shall detect `// indirect` comments on require lines and mark them as indirect
- The parser shall extract `replace` directives with: old module path, old version (optional), new module path or local path, new version (optional)
- The parser shall extract `retract` directives with version or version range and optional comment
- The parser shall handle comments (both `//` line comments and ignoring them during directive extraction)
- When a `require` block is malformed, the parser shall extract what it can and skip unparseable lines

### go.work Parsing
- The parser shall extract the `go` version directive
- The parser shall extract `use` directives (paths to workspace member modules)
- The parser shall extract `replace` directives (same format as go.mod)
- The parser shall extract the `toolchain` directive if present

### Surface Model
- `GoModInfo` shall carry: ModulePath, GoVersion, Toolchain, Requirements[], Replacements[], Retractions[], Uses[] (go.work only)
- `GoModRequirement` shall carry: ModulePath, Version, IsIndirect
- `GoModReplacement` shall carry: OldPath, OldVersion, NewPath, NewVersion, IsLocalPath
- `GoModRetraction` shall carry: Low (version), High (version, same as Low for single), Comment
- `GoModUse` shall carry: Path

### Materialization — go.mod
- The materializer shall create one `document` node with `language: "go.mod"`, `module_path`, `go_version`, `toolchain` (if present), `line_count`, `byte_size`
- The materializer shall create `DEPENDS_ON` edges from the document for each requirement with props: `target` (module path), `version`, `indirect` ("true"/"false")
- `DEPENDS_ON` edges shall be reference edges: `IsComposition = false`, `DstId = null`
- The materializer shall create `go.mod_replace` annotations for each replacement with: `old_path`, `old_version`, `new_path`, `new_version`, `is_local_path`
- The materializer shall create `go.mod_retract` annotations for each retraction

### Materialization — go.work
- The materializer shall create one `document` node with `language: "go.work"`, `go_version`, `toolchain` (if present), `line_count`, `byte_size`
- The materializer shall create `go.work_use` annotations for each `use` directive with the workspace member path
- The materializer shall create `go.mod_replace` annotations for each replacement (same format as go.mod)

### X-Ray Summaries
- go.mod headline: `go.mod | code.go.mod | {line_count} ln | module:{module_path} | {direct_count} direct, {indirect_count} indirect deps`
- go.mod structure shall list: module path, Go version, direct dependencies, indirect dependencies (summarized if many), replacements
- go.work headline: `go.work | code.go.work | {line_count} ln | {use_count} workspace modules`
- go.work structure shall list: Go version, use paths, replacements

### SQL Views
- `go_dependencies` shall show: file_uri, module_path, version, is_indirect, dependency_count (per go.mod)
- `go_dependencies` shall query `DEPENDS_ON` edges joined to document nodes where `language = 'go.mod'`
- `go_replaces` shall show: file_uri, old_path, old_version, new_path, new_version, is_local_path
- `go_replaces` shall query `go.mod_replace` annotations
- Views shall be added to the existing `go_views.sql` embedded resource

### Test Fixtures
- `Fixtures/go.mod` — module with direct and indirect dependencies, replacements, retraction
- `Fixtures/go.work` — workspace with multiple use paths and a replacement
- `Fixtures/go_mod_minimal.mod` — minimal go.mod (just module and go version)
- `Fixtures/go_mod_complex.mod` — go.mod with multiple require blocks, mixed single/block syntax, comments

### Tests
- The parser shall correctly distinguish direct from indirect dependencies
- The parser shall correctly parse local path replacements (e.g., `replace foo => ../bar`)
- The parser shall handle mixed single-line and block directives in the same file
- The parser shall produce partial results when encountering malformed lines
- Round-trip test: parse → materialize → query `go_dependencies` view

## Constraints

- **No tree-sitter** — go.mod/go.work have a simple, well-specified format. Line-scanning with a state machine is sufficient and more readable than a grammar-based approach. tree-sitter-go does not parse go.mod
- **No version resolution** — the parser records declared versions, not resolved versions. `go.sum` parsing and version resolution are extension points
- **Replace detection is literal** — `is_local_path` is true when the new path starts with `.` or `/` or doesn't contain a dot (relative path heuristic). No filesystem check
- **Retractions are informational** — stored as annotations for visibility, not used in dependency analysis

## References

- [Go Format Design](../../designs/future/go-format.md) — go.mod / go.work Parsing section
- [Go North Star](../../north-star/formats/go.md) — module metadata queries
- [Go Modules Reference](https://go.dev/ref/mod) — official go.mod specification
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Malformed require line | Skip line, log diagnostic, continue parsing |
| Missing module path | `PipelineResult.Error` — go.mod without module path is fundamentally broken |
| Replace directive with unexpected format | Skip replacement, log diagnostic |
| Block directive never closed (missing `)`) | Parse what's available, log diagnostic |
| go.work use path doesn't exist on disk | Store the path anyway — validation is not the parser's concern |
| Encoding issues | `PipelineResult.Error` with diagnostic |
