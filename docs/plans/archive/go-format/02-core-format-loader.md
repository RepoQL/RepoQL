---
description: Plan for Go format — classification, surface model population, visibility, materialization, X-ray summaries, DI registration, and core SQL views
tags: [format, go, golang, plan, loader, materialization, views]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Go — Core Format Loader

Implements: [Go Format Design](../../designs/future/go-format.md) — Classification, Graph Materialization (document + go.type + go.member + go.function + HAS_PART + IMPORTS + EMBEDS), X-Ray Summaries, SQL Views (go_types, go_functions, go_methods, go_imports, go_fields), Project Structure (DI registration)

## Scope

**Covers:**
- `GoClassifier` — pipeline processor for media type assignment
- `GoMediaTypes` — media type constants
- `GoConstants` — node kinds, edge types, property key constants
- `GoLoader` — `IFormatLoader`, `IFormatMaterializer`, `IFormatSchemaProvider`
- `GoParser` — `IAsyncPipeline<IClassifiedArtifact, Records?>`
- `GoDocumentState` — state transfer between load and materialize
- Materialization: `document`, `go.type`, `go.member`, `go.function` nodes with `HAS_PART`, `IMPORTS`, and `EMBEDS` edges
- X-ray headline and structure generation
- `go_views.sql` with `go_types`, `go_functions`, `go_methods`, `go_imports`, `go_fields` views
- Shared `functions.sql` update: add `go.member`, `go.function`
- `GoServiceCollectionExtensions.AddGoFormat()` and registration in `AddRepoIndexer()`
- Tests: classification, load + materialize round-trips, X-ray output

**Does not cover:**
- Type definitions and aliases (Plan: 03-extended-structure)
- Constants, iota/enum detection (Plan: 03-extended-structure)
- Package-level variables (Plan: 03-extended-structure)
- Compiler directives (Plan: 03-extended-structure)
- Test function detection (Plan: 03-extended-structure)
- go.mod / go.work parsing (Plan: 04-module-metadata)
- Interface satisfaction computation (Plan: 05-interface-satisfaction)

## Enables

Once this exists:
- **Go files are queryable** — `SELECT * FROM Types WHERE lang = 'go'` returns structs and interfaces
- **Shared views work** — Go types appear in the cross-format `Types` view; Go methods and functions appear in `Functions`
- **Explore finds Go** — `explore(keywords="go struct handler")` returns headlines with type names, method lists, token counts
- **Read shows structure** — `read("file:///server.go => structure", 1000)` shows indented outline with visibility symbols
- **Symbol navigation works** — `read("file:///server.go#symbol=Serve")` resolves through node name matching
- **Embedding visible** — `SELECT * FROM go_fields WHERE is_embedded` shows struct embedding relationships
- **Plans 03–05 can proceed** — all build on the loader, surface model, and materialization pipeline

This is the value-delivery increment. After this, agents can work with Go codebases.

## Prerequisites

- Plan 01 complete — `GoTreeSitterClient` operational and tested
- `GoTreeSitterClient.Parse()` returns `GoDocumentSurface` with byte ranges for all structural elements

## North Star

Index a Go file. Query its types and functions through the same SQL surface as C#, TypeScript, PHP, and Ruby. See the structure without reading the file. The first query an agent tries should work.

## Done Criteria

### Classification
- The classifier shall assign `text/x-go` with kind `code.go` for `.go` files (including `*_test.go`)
- The classifier shall assign `text/x-go-mod` with kind `code.go.mod` for `go.mod` files
- The classifier shall assign `text/x-go-work` with kind `code.go.work` for `go.work` files
- When file has unrecognized extension, the classifier shall call `next()`
- `*_test.go` files share the `code.go` media type — test nature is captured per-function via annotations in Plan 03, not per-file via classification

### Constants
- `GoConstants` shall define node kinds: `Go.Type = "go.type"`, `Go.Member = "go.member"`, `Go.Function = "go.function"`
- `GoConstants` shall define edge types: `IMPORTS`, `EMBEDS`, `HAS_PART`
- `GoConstants` shall define property keys matching shared view conventions: `name`, `qualified_name`, `kind`, `accessibility`, `declaring_type`, `is_static`, `parameters`, `return_type`, `signature`, `is_exported`, `receiver`, `receiver_type`, `is_pointer_receiver`, `tag`, `field_type`, `is_embedded`, `package_name`

### Materialization — Document Node
- The materializer shall create one `document` node with `language: "go"`, `line_count`, `byte_size`, `package_name`
- `package_name` shall be set from `GoDocumentSurface.PackageName`

### Materialization — Type Nodes
- The materializer shall create `go.type` nodes for structs with props: `name`, `qualified_name` (PackageName.TypeName), `kind: "struct"`, `accessibility` ("public"/"private"), `is_exported`
- The materializer shall create `go.type` nodes for interfaces with props: `name`, `qualified_name`, `kind: "interface"`, `accessibility`, `is_exported`
- `accessibility` shall be `"public"` when the type name starts with an uppercase letter, `"private"` otherwise

### Materialization — Member Nodes (Methods)
- The materializer shall create `go.member` nodes for methods with props: `name`, `qualified_name` (PackageName.ReceiverType.MethodName), `kind: "method"`, `declaring_type` (receiver type name), `accessibility`, `is_exported`, `is_static: "false"`, `receiver` (receiver variable name), `receiver_type`, `is_pointer_receiver`, `parameters` (text), `return_type` (text), `signature`
- `signature` shall be the full method signature (e.g., `func (*Server) Serve(addr string) error`)

### Materialization — Member Nodes (Fields)
- The materializer shall create `go.member` nodes for struct fields with props: `name`, `qualified_name` (PackageName.StructName.FieldName), `kind: "field"`, `declaring_type` (struct name), `accessibility`, `is_exported`, `field_type` (type as text), `tag` (raw tag string or null), `is_embedded`
- Embedded fields shall have `is_embedded: "true"` and use the type name as the field name
- Embedded fields shall additionally generate `EMBEDS` edges from the struct's `go.type` node

### Materialization — Function Nodes
- The materializer shall create `go.function` nodes for top-level functions with props: `name`, `qualified_name` (PackageName.FunctionName), `kind: "function"`, `accessibility`, `is_exported`, `is_static: "false"`, `parameters` (text), `return_type` (text), `signature`

### Materialization — Edges
- The materializer shall create `HAS_PART` composition edges from document to types and top-level functions
- The materializer shall create `HAS_PART` composition edges from types to their fields
- The materializer shall attach methods to their receiver type via `HAS_PART` when the type exists in the same document
- When a method's receiver type is not found in the document, the method shall be attached to the document node via `HAS_PART`
- `HAS_PART` edges shall carry `ordinal` reflecting source order
- The materializer shall create `IMPORTS` edges from the document node for each import spec with props: `target` (import path), `alias` (or null), `import_category` (stdlib/internal/external)
- The materializer shall create `EMBEDS` edges from struct `go.type` nodes for embedded fields with props: `target` (embedded type name)
- `IMPORTS` and `EMBEDS` edges shall be reference edges: `IsComposition = false`, `DstId = null`

### Materialization — Spans
- Each node shall have a span with 1-based line numbers and 0-based byte offsets, created via `DocumentModel.LineMap.GetSpan()`

### X-Ray Summaries
- The headline shall follow: `{filename} | code.go | {line_count} ln, ~{token_count} tok | pkg:{package} | {primary_declarations} | {key_names}`
- When file has one struct, primary_declarations shall be the struct name
- When file has one interface, primary_declarations shall be `interface {name}`
- When file has a `main` function, primary_declarations shall be `func main`
- When file has multiple declarations, primary_declarations shall summarize (e.g., `3 types, 2 funcs`)
- Key names shall list up to 8 exported type/function/method names
- The structure shall show indented outline with visibility symbols: `+` exported, `-` unexported
- The structure shall show receiver information for methods (e.g., `+ func (*Server) Serve(addr string) error`)
- Fields shall appear indented under their struct (e.g., `+ field DB *sql.DB`)
- The structure shall include `#symbol=` anchors for each type, method, function, and field
- X-ray shall be built in C# (no Liquid templates — following Ruby/PHP convention)

### SQL Views
- `go_types` shall show: uri, file_uri, file_name, name, qualified_name, type_kind (struct/interface), package_name, visibility, is_exported, field_count (for structs), method_count, start_line, end_line, headline, structure, node_id, span_id
- `go_functions` shall show: uri, file_uri, file_name, name, qualified_name, package_name, visibility, is_exported, signature, parameters, return_type, start_line, end_line, headline, node_id, span_id
- `go_methods` shall show: uri, file_uri, file_name, name, qualified_name, declaring_type, package_name, visibility, is_exported, receiver, receiver_type, is_pointer_receiver, signature, parameters, return_type, start_line, end_line, headline, node_id, span_id
- `go_imports` shall show: file_uri, file_name, import_path, alias, import_category, package_name
- `go_imports` shall query `IMPORTS` edges joined to document nodes — no `go.import` node kind exists
- `go_fields` shall show: uri, file_uri, struct_name, struct_qualified_name, name, field_type, tag, is_embedded, is_exported, start_line, end_line, node_id, span_id
- Views shall be embedded as `Schema/go_views.sql` and registered via `IFormatSchemaProvider`

### Shared View Integration
- `functions.sql` shall include `'go.member'` and `'go.function'` in the node kind filter
- Go members with `kind: "method"` and functions with `kind: "function"` match the existing `$.kind` property filter — no change needed to the kind value list
- Fields (`kind: "field"`) are correctly excluded by the `$.kind` filter
- `go.type` nodes shall appear in the shared `Types` view via the existing `%.type` pattern match (no change needed)

### DI Registration
- `AddGoFormat()` shall register `GoLoader` as `IFormatSchemaProvider`
- `AddGoFormat()` shall register a `FormatDescriptor` for `.go` extension and `go.mod`, `go.work` filenames
- `AddGoFormat()` shall register `GoParser` as an indexing processor
- `AddGoFormat()` shall be called from `AddRepoIndexer()` in `RepoIndexerServiceCollectionExtensions`

### State Transfer
- `GoDocumentState` shall carry the `GoDocumentSurface`, digest, size, media type, and store URI
- State shall be set in `DocumentModel.Metadata` during loading and consumed during materialization

## Constraints

- **Follow Ruby/PHP pattern** — loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, schema registration via `IFormatSchemaProvider`. Mirror the established pattern
- **X-ray built in C#** — no Liquid templates; build headline and structure strings directly, following Ruby/PHP convention
- **Property names match shared views exactly** — `name`, `qualified_name`, `kind`, `accessibility`, `declaring_type`, `is_static`, `parameters`, `return_type`, `signature`. Deviation breaks cross-format queries
- **No constants, variables, or directives in this increment** — fields and embedding are included (they're part of the struct structure), but constants, variables, type definitions, and directives are Plan 03
- **`return_type` is a text string** — Go return types can be complex (multiple returns, named returns). Stored as the raw text. Structured return type parsing is an extension point
- **`is_static` is always `"false"` for Go members** — Go has no static methods, only top-level functions. Functions are `go.function` nodes, not `go.member` with `is_static: true`. This matches the convention where `is_static` on `go.member` is always false
- **go.mod/go.work classification only** — the classifier identifies these files but Plan 02 does not parse them. Parsing is Plan 04's scope. Classified but unloaded files produce empty results

## References

- [Go Format Design](../../designs/future/go-format.md) — full architecture
- [Ruby Format Loader](../../../src/Formats/RepoQL.Formats.Ruby/RubyLoader.cs) — reference implementation for loader/materializer pattern
- [Ruby DI Registration](../../../src/Formats/RepoQL.Formats.Ruby/RubyServiceCollectionExtensions.cs) — registration pattern
- [PHP Format Loader](../../../src/Formats/RepoQL.Formats.PHP/PHPLoader.cs) — alternative reference for materialization
- [Global Registration](../../../src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs) — where `AddGoFormat()` is called
- [Shared Functions View](../../../src/RepoQL.Data.DuckDB/Schema/Views/functions.sql) — add `go.member`, `go.function`
- [Shared Types View](../../../src/RepoQL.Data.DuckDB/Schema/Views/types.sql) — validates `%.type` pattern match
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor patterns
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Each extraction phase (package, imports, structs, interfaces, functions, methods) is independently try/caught. A malformed struct definition must never prevent function extraction in the same file.

| Failure | Behavior |
|---------|----------|
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| Tree-sitter returns ERROR nodes | Skip error regions, extract surrounding structure |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Method receiver type not found in document | Attach method to document via HAS_PART, set `declaring_type` property |
| Qualified name computation fails | Use simple name as qualified_name |
| go.mod / go.work file classified but not parseable yet | Return empty result (Plan 04 adds parsing) |
