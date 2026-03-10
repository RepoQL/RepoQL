---
description: Plan for Rust format — classification, surface model, visibility, same-file impl dissolution, materialization, X-ray summaries, DI registration, and basic SQL views
tags: [format, rust, plan, loader, materialization, views]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Rust — Core Format Loader

Implements: [Rust Format Design](../../designs/future/rust-format.md) — Classification, Surface Model, Visibility Tracking, Impl Block Resolution (same-file only), Graph Materialization (document + rs.type + rs.member + rs.function + rs.module + HAS_PART), X-Ray Summaries, SQL Views (rust_types, rust_functions, rust_methods, rust_modules), Project Structure (DI registration)

## Scope

**Covers:**
- `RustClassifier` — pipeline processor for media type assignment
- `RustMediaTypes` — media type constants
- `RustConstants` — node kinds, edge types, property key constants
- `RustDocumentSurface` population from `RustTreeSitterClient` parse results
- Visibility normalization (`pub` → "public", `pub(crate)` → "pub_crate", etc.)
- Same-file impl block dissolution — methods from impl blocks parent to the target type's `rs.type` node
- `RustLoader` — `IFormatLoader`, `IFormatMaterializer`, `IFormatSchemaProvider`
- `RustParser` — `IAsyncPipeline<IClassifiedArtifact, Records?>`
- `RustDocumentState` — state transfer between load and materialize
- Materialization: `document`, `rs.type`, `rs.member`, `rs.function`, `rs.module` nodes with `HAS_PART` edges
- Fields, variants, associated types, associated consts as JSON properties on `rs.type`
- Constants and statics visible in structure only (not materialized as nodes)
- X-ray headline and structure generation (with doc comments always included)
- `rust_views.sql` with `rust_types`, `rust_functions`, `rust_methods`, `rust_modules` views
- Shared `functions.sql` update: add `rs.member`, `rs.function`
- `RustServiceCollectionExtensions.AddRustFormat()` and registration in `AddRepoIndexer()`
- Tests: classification, load + materialize round-trips, visibility, same-file impl dissolution, X-ray output, shared view participation

**Does not cover:**
- Cross-file impl block resolution — stub `rs.type` nodes (Plan: 03-trait-graph)
- IMPLEMENTS, EXTENDS, DERIVES edges (Plan: 03-trait-graph)
- rust_impls, rust_derives, rust_unsafe views (Plan: 03-trait-graph)
- IMPORTS edges and use declaration tracking (Plan: 04-imports-macros)
- rs.macro nodes for macro_rules! definitions (Plan: 04-imports-macros)
- rs.macro_expansion annotations and honesty surface (Plan: 04-imports-macros)
- rust_imports, rust_macros, rust_macro_expansion views (Plan: 04-imports-macros)

## Enables

Once this exists:
- **Rust files are queryable** — `SELECT * FROM Types WHERE lang = 'rs'` returns structs, enums, traits
- **Shared views work** — Rust types appear in the cross-format `Types` view; Rust methods appear in `Functions`
- **Explore finds Rust** — `explore(keywords="rust struct")` returns headlines with type names, member lists, token counts
- **Read shows structure** — `read("file:///src/pool.rs => structure", 1000)` shows indented outline with visibility symbols and doc comments
- **Symbol navigation works** — `read("file:///pool.rs#symbol=ConnectionPool.connect")` resolves through node name matching
- **Same-file impls work** — methods from `impl Foo {}` in the same file as `struct Foo` parent correctly to the Foo type node
- **Plans 03-04 can proceed** — all build on the loader, surface model, and materialization pipeline

This is the value-delivery increment. After this, agents can work with Rust codebases.

## Prerequisites

- Plan 01 complete — `RustTreeSitterClient` operational and tested
- `RustTreeSitterClient.Parse()` returns surface model types with byte ranges for all structural elements

## North Star

Index a Rust file. Query its structs, enums, traits, and functions through the same SQL surface as C#, TypeScript, PHP, and Ruby. See the structure — including doc comments — without reading the file. When impl blocks are in the same file as the type, the agent sees one unified type with all its methods. The first query an agent tries should work.

## Done Criteria

### Classification
- The classifier shall assign `text/x-rust` with kind `code.rust` for `.rs` files
- The classifier shall assign kind `code.rust.build` for `build.rs` files
- When file has unrecognized extension, the classifier shall call `next()`

### Surface Model Population
- The parser shall produce a `RustDocumentSurface` from `RustTreeSitterClient` parse results
- Structs shall carry: name, visibility, generics, where_clause, derives (comma-separated string), attributes, byte range, fields[], doc comment
- Enums shall carry: name, visibility, generics, where_clause, derives, attributes, byte range, variants[], doc comment
- Enum variants shall carry: name, variant_kind (unit/tuple/struct), fields[], discriminant, byte range, doc comment
- Traits shall carry: name, visibility, generics, where_clause, supertraits, is_auto, is_unsafe, byte range, methods[], associated_types[], associated_consts[], doc comment
- Impl blocks shall carry: target_type, trait_name (null for inherent), generics, where_clause, is_unsafe, byte range, methods[], associated_types[], associated_consts[]
- Methods shall carry: name, visibility, is_async, is_unsafe, is_const, self_kind (self/&self/&mut self/none), parameters, return_type, byte range, doc comment
- Free functions shall carry: name, visibility, is_async, is_unsafe, is_const, generics, parameters, return_type, byte range, is_test (from `#[test]` attribute), doc comment
- Modules shall carry: name, visibility, is_inline, byte range, doc comment
- Constants shall carry: name, visibility, const_type, byte range, doc comment
- Statics shall carry: name, visibility, static_type, is_mutable, byte range, doc comment
- Type aliases shall carry: name, visibility, generics, aliased_type, byte range
- Unions shall carry: name, visibility, generics, derives, byte range, fields[]
- `Stats` shall carry: struct_count, enum_count, trait_count, impl_count, function_count, line_count

### Visibility Normalization
- `pub` shall normalize to `"public"`
- `pub(crate)` shall normalize to `"pub_crate"`
- `pub(super)` shall normalize to `"pub_super"`
- `pub(in path)` shall normalize to `"pub_in:{path}"`
- Absent visibility shall normalize to `"private"`
- The property shall be named `accessibility` to match the shared view contract

### Same-File Impl Block Dissolution
- When an impl block's target type is defined in the same file, the materializer shall parent methods to the target type's `rs.type` node via HAS_PART edges
- When multiple impl blocks target the same type in the same file, all methods shall parent to the same `rs.type` node
- Trait impl methods shall carry `impl_trait` prop set to the trait name
- Method `declaring_type` prop shall be set to the target type name
- When an impl block's target type is NOT in the same file, the materializer shall parent methods directly to the document node with `declaring_type` set — this is the temporary behavior for Plan 02. Plan 03 replaces this with stub `rs.type` nodes

### Materialization
- The materializer shall create one `document` node with `language: "rust"`, `line_count`, `byte_size`
- The materializer shall create `rs.type` nodes for structs with props: `name`, `qualified_name`, `kind: "struct"`, `accessibility`, `generics`, `where_clause`, `derives`, `fields` (JSON array), `is_stub: "false"`
- The materializer shall create `rs.type` nodes for enums with props: `kind: "enum"`, `variants` (JSON array)
- The materializer shall create `rs.type` nodes for traits with props: `kind: "trait"`, `extends` (supertraits), `is_auto`, `is_unsafe`, `associated_types` (JSON), `associated_consts` (JSON)
- The materializer shall create `rs.type` nodes for unions with props: `kind: "union"`, `fields` (JSON array)
- The materializer shall create `rs.type` nodes for type aliases with props: `kind: "type_alias"`
- The materializer shall create `rs.member` nodes for methods with props: `name`, `qualified_name`, `kind: "method"`, `declaring_type`, `accessibility`, `is_async`, `is_unsafe`, `is_const`, `is_static`, `self_kind`, `parameters`, `return_type`, `impl_trait`
- The materializer shall create `rs.function` nodes for free functions with props: `name`, `qualified_name`, `kind: "function"`, `accessibility`, `is_async`, `is_unsafe`, `is_const`, `is_static: "true"`, `generics`, `parameters`, `return_type`, `is_test`
- The materializer shall create `rs.module` nodes for module declarations with props: `name`, `qualified_name`, `accessibility`, `is_inline`
- Constants and statics shall NOT be materialized as nodes — they appear in structure only
- The materializer shall create `HAS_PART` composition edges from document to types, free functions, and modules
- The materializer shall create `HAS_PART` composition edges from types to their methods
- `HAS_PART` edges shall carry `ordinal` reflecting source order
- Each node shall have a span with 1-based line numbers and 0-based byte offsets, created via `DocumentModel.LineMap.GetSpan()`

### `is_static` Semantics
- For `rs.member`: `is_static` shall be `"true"` when the method has no `self` parameter (associated function); `"false"` when it has `self`, `&self`, or `&mut self`
- For `rs.function`: `is_static` shall always be `"true"` (free functions are not bound to an instance)

### X-Ray Summaries
- The headline shall follow: `{filename} | {primary_declaration} | {key_members} | {line_count} ln, ~{token_count} tok`
- When file has one struct/enum/trait, primary_declaration shall be the type signature including generics
- When file has multiple top-level declarations, primary_declaration shall summarize
- Key members shall list up to 8 public method names
- The structure shall show indented outline with visibility symbols: `+` public, `~` pub(crate), `#` pub(super), `-` private
- The structure shall include doc comments (`///`) on every item that has them in source — always, no conditional logic
- The structure shall include `#symbol=` anchors for each type and method
- The structure shall show `derives:` line under types that have derives
- The structure shall show fields with visibility symbols and types
- Trait impl sections shall be grouped under `impl TraitName` headers
- Constants and statics shall appear in structure with their types

### SQL Views
- `rust_types` shall show `qualified_name`, `name`, `type_kind`, `visibility`, `generics`, `derives`, `supertraits`, `is_unsafe`, `structure` — selected from `rs.type` nodes
- `rust_functions` shall show `file_uri`, `function_uri`, `headline`, `name`, `qualified_name`, `visibility`, `is_async`, `is_unsafe`, `is_const`, `is_test`, `generics`, `parameters`, `return_type`
- `rust_methods` shall show `file_uri`, `parent_uri`, `parent_name`, `parent_qualified_name`, `method_uri`, `headline`, `name`, `declaring_type`, `visibility`, `is_async`, `is_unsafe`, `is_const`, `is_static`, `self_kind`, `parameters`, `return_type`, `impl_trait` — using two-hop join (document → type → method)
- `rust_modules` shall show `file_uri`, `module_uri`, `name`, `qualified_name`, `visibility`, `is_inline`
- Views shall be embedded as `Schema/rust_views.sql` and registered via `IFormatSchemaProvider`

### Shared View Integration
- `functions.sql` shall include `'rs.member'` and `'rs.function'` in the node kind filter
- Rust methods with `kind: "method"` and free functions with `kind: "function"` already match the existing `$.kind` property filter — no change needed to the kind value list
- `rs.type` nodes shall appear in the shared `Types` view via the existing `%.type` pattern match (no change needed)

### DI Registration
- `AddRustFormat()` shall register `RustLoader` as `IFormatSchemaProvider`
- `AddRustFormat()` shall register a `FormatDescriptor` for `.rs` extension and `build.rs` filename
- `AddRustFormat()` shall register `RustParser` as an indexing processor
- `AddRustFormat()` shall be called from `AddRepoIndexer()` in `RepoIndexerServiceCollectionExtensions`

### State Transfer
- `RustDocumentState` shall carry the `RustDocumentSurface`, digest, size, media type, and store URI
- State shall be set in `DocumentModel.Metadata` during loading and consumed during materialization

## Constraints

- **Follow Ruby/PHP pattern** — loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, schema registration via `IFormatSchemaProvider`. Mirror `RubyLoader` structure
- **X-ray built in C#** — no Liquid templates; build headline and structure strings directly, following Ruby/PHP convention
- **Property names match shared views exactly** — `name`, `qualified_name`, `kind`, `accessibility`, `extends`, `declaring_type`, `is_static`, `parameters`, `return_type`. Deviation breaks cross-format queries
- **No relationship edges in this increment** — `rs.type` nodes exist but IMPLEMENTS/EXTENDS/DERIVES edges are Plan 03. The `extends` prop on trait `rs.type` is set from supertrait names (for display) but the deferred-reference EXTENDS edge is not created yet
- **No import edges in this increment** — use declarations are parsed but IMPORTS edges are Plan 04
- **Cross-file impls use temporary document-parenting** — methods from impl blocks whose target type is not in the same file are parented to the document with `declaring_type` prop. Plan 03 replaces this with stub `rs.type` nodes. The `rust_methods` two-hop view will not show these methods until Plan 03
- **`return_type` is the declared type** — Rust always has explicit return types (or `()` for implicit). Unlike Ruby, this is always populated
- **Doc comments always in structure** — `///` comments appear on every item that has them in source. No conditional logic, no toggle

## References

- [Rust Format Design](../../designs/future/rust-format.md) — full architecture
- [Ruby Format Loader](../../../src/Formats/RepoQL.Formats.Ruby/) — reference implementation for tree-sitter-based loader pattern
- [PHP Format Loader](../../../src/Formats/RepoQL.Formats.PHP/) — reference implementation for loader/parser/materializer pattern
- [Ruby DI Registration](../../../src/Formats/RepoQL.Formats.Ruby/RubyServiceCollectionExtensions.cs) — registration pattern
- [Global Registration](../../../src/Indexing/RepoQL.Indexing/RepoIndexerServiceCollectionExtensions.cs) — where `AddRustFormat()` is called
- [Shared Functions View](../../../src/RepoQL.Data.DuckDB/Schema/Views/functions.sql) — add `rs.member`, `rs.function`
- [Shared Types View](../../../src/RepoQL.Data.DuckDB/Schema/Views/types.sql) — validates `%.type` pattern match
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor patterns
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Each extraction phase (structs, enums, traits, impls, functions, modules) is independently try/caught. A malformed struct definition must never prevent function extraction in another part of the file.

| Failure | Behavior |
|---------|----------|
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| Tree-sitter returns ERROR nodes | Skip error regions, extract surrounding structure |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Visibility modifier unrecognized | Default `accessibility` to `"private"` |
| Impl block target type not in same file | Parent methods to document with `declaring_type` prop (temporary — Plan 03 replaces) |
| Qualified name computation fails (deeply nested generics) | Use simple name as qualified_name |
| Doc comment extraction fails | Continue without comment — item still appears in structure |
