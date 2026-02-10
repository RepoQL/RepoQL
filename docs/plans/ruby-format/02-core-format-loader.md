---
description: Plan for Ruby format — classification, surface model, visibility, materialization, X-ray summaries, DI registration, and basic SQL views
tags: [format, ruby, plan, loader, materialization, views]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Ruby — Core Format Loader

Implements: [Ruby Format Design](../../designs/current/ruby-format.md) — Classification, Surface Model, Visibility Tracking, Graph Materialization (document + rb.type + rb.member + rb.function + HAS_PART), X-Ray Summaries, SQL Views (ruby_types, ruby_methods), Project Structure (DI registration)

## Scope

**Covers:**
- `RubyClassifier` — pipeline processor for media type assignment
- `RubyMediaTypes` — media type constants
- `RubyConstants` — node kinds, edge types, property key constants
- `RubyDocumentSurface` population from `RubyTreeSitterClient` parse results
- Visibility state machine (bare modifiers and method-targeted modifiers)
- `RubyLoader` — `IFormatLoader`, `IFormatMaterializer`, `IFormatSchemaProvider`
- `RubyParser` — `IAsyncPipeline<IClassifiedArtifact, Records?>`
- `RubyDocumentState` — state transfer between load and materialize
- Materialization: `document`, `rb.type`, `rb.member`, `rb.function` nodes with `HAS_PART` edges
- X-ray headline and structure generation
- `ruby_views.sql` with `ruby_types` and `ruby_methods` views
- Shared `functions.sql` update: add `rb.member`, `rb.function`, `singleton_method`
- `RubyServiceCollectionExtensions.AddRubyFormat()` and registration in `AddRepoIndexer()`
- Tests: classification, load + materialize round-trips, visibility, X-ray output

**Does not cover:**
- Mixin edges: INCLUDES, PREPENDS, EXTENDS_MODULE (Plan: 03-mixin-graph)
- EXTENDS edges for superclasses (Plan: 03-mixin-graph)
- Reopening detection (Plan: 03-mixin-graph)
- rb.constant nodes (Plan: 04-namespace-graph)
- REQUIRES and ALIASES edges (Plan: 04-namespace-graph)
- rb.property nodes and metaprogramming patterns (Plan: 05-metaprogramming)
- ASSOCIATES edges and annotation-based views (Plan: 05-metaprogramming)

## Enables

Once this exists:
- **Ruby files are queryable** — `SELECT * FROM Types WHERE lang = 'rb'` returns classes and modules
- **Shared views work** — Ruby types appear in the cross-format `Types` view; Ruby methods appear in `Functions`
- **Explore finds Ruby** — `explore(keywords="ruby class")` returns headlines with class names, member lists, token counts
- **Read shows structure** — `read("file:///app/models/user.rb => structure", 1000)` shows indented outline with visibility symbols
- **Symbol navigation works** — `read("file:///user.rb#symbol=authenticate")` resolves through node name matching
- **Plans 03-05 can proceed** — all build on the loader, surface model, and materialization pipeline

This is the value-delivery increment. After this, agents can work with Ruby codebases.

## Prerequisites

- Plan 01 complete — `RubyTreeSitterClient` operational and tested
- `RubyTreeSitterClient.Parse()` returns surface model types with byte ranges for all structural elements

## North Star

Index a Ruby file. Query its classes and methods through the same SQL surface as C#, TypeScript, and PHP. See the structure without reading the file. The first query an agent tries should work.

## Done Criteria

### Classification
- The classifier shall assign `text/x-ruby` with kind `code.ruby` for `.rb` files
- The classifier shall assign kind `code.ruby.rake` for `.rake` files and `Rakefile`
- The classifier shall assign kind `code.ruby.gemspec` for `.gemspec` files
- The classifier shall assign kind `code.ruby.gemfile` for `Gemfile`
- The classifier shall assign kind `code.ruby` for `Guardfile` and `Dangerfile`
- When file extension is `.erb`, the classifier shall pass through (return null)
- When file has unrecognized extension, the classifier shall call `next()`

### Surface Model Population
- The parser shall produce a `RubyDocumentSurface` from `RubyTreeSitterClient` parse results
- Classes shall carry: name, qualified_name (with nesting), superclass name, has_superclass_declaration, byte range
- Modules shall carry: name, qualified_name (with nesting), nesting depth, byte range
- Methods shall carry: name, visibility, is_static, parameter text, accepts_block, byte range
- Singleton methods on `self` shall be represented as methods with `is_static: true`
- Singleton methods on other objects shall carry: name, receiver name, parameter text, byte range
- Top-level functions shall carry: name, parameter text, accepts_block, byte range
- `Stats` shall carry: class_count, module_count, method_count, line_count

### Visibility State Machine
- The parser shall track a visibility state per class/module scope, defaulting to public
- When bare `private` is encountered, subsequent methods in that scope shall be marked private
- When bare `protected` is encountered, subsequent methods shall be marked protected
- When bare `public` is encountered, subsequent methods shall be marked public
- When `private :method_name` is encountered, only that method shall be marked private (state unchanged)
- When `protected :method_name` is encountered, only that method shall be marked protected
- Nested class/module scopes shall have independent visibility state

### Materialization
- The materializer shall create one `document` node with `language: "ruby"`, `line_count`, `byte_size`
- The materializer shall create `rb.type` nodes for classes with props: `name`, `qualified_name`, `kind: "class"`, `namespace`, `accessibility: "public"`, `extends` (superclass name or null)
- The materializer shall create `rb.type` nodes for modules with props: `name`, `qualified_name`, `kind: "module"`, `namespace`, `accessibility: "public"`
- The materializer shall create `rb.member` nodes for instance methods with props: `name`, `qualified_name`, `kind: "method"`, `declaring_type`, `accessibility`, `is_static: "false"`, `parameters`, `accepts_block`
- The materializer shall create `rb.member` nodes for class methods with props: `kind: "method"`, `is_static: "true"`
- The materializer shall create `rb.member` nodes for singleton methods on objects with props: `kind: "singleton_method"`, `is_static: "true"`, `receiver`
- The materializer shall create `rb.function` nodes for top-level functions with props: `name`, `kind: "function"`, `parameters`, `accepts_block`
- The materializer shall create `HAS_PART` composition edges from document to types and top-level functions
- The materializer shall create `HAS_PART` composition edges from types to their members
- `HAS_PART` edges shall carry `ordinal` reflecting source order
- Each node shall have a span with 1-based line numbers and 0-based byte offsets, created via `DocumentModel.LineMap.GetSpan()`

### X-Ray Summaries
- The headline shall follow: `{filename} | {primary_declaration} | {key_members} | {line_count} ln, ~{token_count} tok`
- When file has one class, primary_declaration shall be the class signature (e.g., `class User < ApplicationRecord`)
- When file has one module, primary_declaration shall be the module name
- When file has multiple top-level declarations, primary_declaration shall summarize (e.g., `3 classes`)
- Key members shall list up to 8 public method names
- The structure shall show indented outline with visibility symbols: `+` public, `#` protected, `-` private
- The structure shall include `#symbol=` anchors for each method
- Block-accepting methods shall show `&block` in their parameter list

### SQL Views
- `ruby_types` shall show `qualified_name`, `type_kind`, `extends`, `definition_count`, `structure` — aggregated by `n.properties->>'qualified_name'` (not a bare column name). The `defined_in`, `origin_file`, and `is_reopening`-dependent logic are deferred to Plan 03, which replaces this view with the full reopening-aware version
- `ruby_methods` shall show `document_uri`, `type_uri`, `type_name`, `type_qualified_name`, `method_uri`, `headline`, `name`, `visibility`, `is_class_method`, `accepts_block`, `is_generated`, `generator`, `parameters`
- Views shall be embedded as `Schema/ruby_views.sql` and registered via `IFormatSchemaProvider`

### Shared View Integration
- `functions.sql` shall include `'rb.member'` and `'rb.function'` in the node kind filter
- Ruby methods with `kind: "method"` and functions with `kind: "function"` already match the existing `$.kind` property filter — no change needed to the kind value list
- Singleton methods (`kind: "singleton_method"`) are intentionally excluded from the shared Functions view — they are Ruby-specific and appear only in `ruby_methods`
- `rb.type` nodes shall appear in the shared `Types` view via the existing `%.type` pattern match (no change needed)

### DI Registration
- `AddRubyFormat()` shall register `RubyLoader` as `IFormatSchemaProvider`
- `AddRubyFormat()` shall register a `FormatDescriptor` for `.rb`, `.rake`, `.gemspec` extensions and `Gemfile`, `Rakefile`, `Guardfile`, `Dangerfile` filenames
- `AddRubyFormat()` shall register `RubyParser` as an indexing processor
- `AddRubyFormat()` shall be called from `AddRepoIndexer()` in `RepoIndexerServiceCollectionExtensions`

### State Transfer
- `RubyDocumentState` shall carry the `RubyDocumentSurface`, digest, size, media type, and store URI
- State shall be set in `DocumentModel.Metadata` during loading and consumed during materialization

## Constraints

- **Follow PHP pattern** — loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, schema registration via `IFormatSchemaProvider`. Mirror `PHPLoader` structure
- **X-ray built in C#** — no Liquid templates; build headline and structure strings directly, following PHP convention
- **Property names match shared views exactly** — `name`, `qualified_name`, `kind`, `accessibility`, `extends`, `declaring_type`, `is_static`, `parameters`, `return_type`. Deviation breaks cross-format queries
- **No mixin or inheritance edges in this increment** — `rb.type` nodes exist but EXTENDS/INCLUDES/PREPENDS edges are Plan 03. The `extends` prop on `rb.type` is set from the superclass name (for display in ruby_types view) but the deferred-reference EXTENDS edge is not created yet
- **Visibility only for methods in this increment** — all types are `accessibility: "public"` (Ruby types are always public at the language level)
- **`return_type` is null for Ruby methods** — Ruby has no type annotations on method signatures. The shared Functions view projects `return_type`; for Ruby it will always be null. RBS support is an extension point

## References

- [Ruby Format Design](../../designs/current/ruby-format.md) — full architecture
- [PHP Format Loader](../../../src/Formats/RepoQL.Formats.PHP/) — reference implementation for loader/parser/materializer pattern
- [PHP DI Registration](../../../src/Formats/RepoQL.Formats.PHP/PHPServiceCollectionExtensions.cs) — registration pattern
- [Global Registration](../../../src/Indexing/RepoQL.Indexing/RepoIndexerServiceCollectionExtensions.cs) — where `AddRubyFormat()` is called
- [Shared Functions View](../../../src/RepoQL.Data.DuckDB/Schema/Views/functions.sql) — add `rb.member`, `rb.function`
- [Shared Types View](../../../src/RepoQL.Data.DuckDB/Schema/Views/types.sql) — validates `%.type` pattern match
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor patterns
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Each extraction phase (classes, modules, methods, functions) is independently try/caught. A malformed class definition must never prevent method extraction in another class.

| Failure | Behavior |
|---------|----------|
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| Tree-sitter returns ERROR nodes | Skip error regions, extract surrounding structure |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Visibility tracking ambiguous | Default to `public` |
| Qualified name computation fails (deeply nested) | Use simple name as qualified_name |
