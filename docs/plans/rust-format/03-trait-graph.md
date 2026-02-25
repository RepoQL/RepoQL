---
description: Plan for Rust format — trait implementation edges, derive extraction, cross-file impl stub nodes, and trait/derive/unsafe views
tags: [format, rust, plan, traits, derives, impls, unsafe]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Rust — Trait Graph, Derives, and Cross-File Impls

Implements: [Rust Format Design](../../designs/future/rust-format.md) — Graph Materialization (IMPLEMENTS, EXTENDS, DERIVES edges), Impl Block Resolution (stub rs.type nodes), Cross-Cutting Concerns (Scattered implementations, Deferred references), SQL Views (rust_impls, rust_derives, rust_unsafe)

## Scope

**Covers:**
- IMPLEMENTS edges from types to traits (from trait impl blocks)
- EXTENDS edges from traits to supertraits
- DERIVES edges from types to derived traits (+ `derives` prop already set in Plan 02)
- Stub `rs.type` nodes for cross-file impl blocks (replacing Plan 02's temporary document-parenting)
- `rs.macro_expansion` annotations for derive macros specifically (derive honesty is tightly coupled to derive edges)
- Updated `rust_types` view with `definition_count` and `defined_in` (aggregated across files)
- SQL views: `rust_impls`, `rust_derives`, `rust_unsafe`
- Tests: cross-file impl resolution, derive edge creation, supertrait edges, unsafe view, shared view participation of stub nodes

**Does not cover:**
- IMPORTS edges and use declaration tracking (Plan: 04-imports-macros)
- rs.macro nodes for macro_rules! definitions (Plan: 04-imports-macros)
- Non-derive macro invocation annotations (Plan: 04-imports-macros)
- Proc-macro attribute annotations (Plan: 04-imports-macros)
- rust_imports, rust_macros, rust_macro_expansion views (Plan: 04-imports-macros)

## Enables

Once this exists:
- **Trait implementation queries work** — `SELECT * FROM rust_impls WHERE trait_name = 'Storage'` finds every type implementing Storage
- **Derive queries work** — `SELECT * FROM rust_derives WHERE derived_trait = 'Serialize'` finds every type with `#[derive(Serialize)]`
- **Unsafe surface is queryable** — `SELECT * FROM rust_unsafe` shows every unsafe function, method, trait, and trait impl in one query
- **Cross-file impls are unified** — methods from impl blocks in separate files parent to stub `rs.type` nodes that participate in shared views and aggregate correctly in `rust_types`
- **Supertrait hierarchy is traversable** — EXTENDS edges from traits to supertraits enable `SELECT * FROM rust_impls WHERE trait_name IN (SELECT supertraits FROM rust_types WHERE name = 'Read')`
- **The graph is Rust-shaped** — trait impls, derives, and scattered impl blocks are the queries agents ask about Rust code

This is what makes Rust queryable as Rust, not just "another language with types and functions."

## Prerequisites

- Plan 02 complete — `rs.type`, `rs.member`, `rs.function` nodes exist, `RustDocumentSurface` populated, materialization pipeline operational
- `RustTreeSitterClient` already extracts impl blocks, trait definitions, derive attributes, and supertraits (Plan 01 queries)

## North Star

When a type implements three traits across two files, the agent sees all three IMPLEMENTS edges in one query. When a struct derives Serialize, Debug, and Clone, the agent finds it via any of those traits. When a class has methods scattered across impl blocks in different files, they all parent to type nodes that aggregate into one unified picture. The trait graph is the central organizing principle of Rust code — and it's one query away.

## Done Criteria

### Stub rs.type Nodes for Cross-File Impls
- When an impl block's target type is NOT defined in the same file, the materializer shall create a stub `rs.type` node for the target type
- The stub node shall have `is_stub: "true"` and `kind` matching the target (default to `"struct"` when unknown)
- The stub node shall have `name` and `qualified_name` set to the target type name
- Methods from the cross-file impl block shall parent to the stub `rs.type` node via HAS_PART edges
- The stub `rs.type` node shall parent to the document via HAS_PART edge
- This replaces Plan 02's temporary document-parenting of cross-file methods
- The `rust_methods` two-hop view (document → type → method) shall now find all methods, including cross-file impl methods
- Stub `rs.type` nodes shall appear in the shared `Types` view via `%.type` pattern match

### IMPLEMENTS Edges
- The materializer shall create IMPLEMENTS edges from `rs.type` to trait for each trait impl block
- When the impl block is `impl Display for Foo`, the source shall be the Foo `rs.type` node (local or stub) and the edge shall carry `target: "Display"` in props
- IMPLEMENTS edges shall be reference edges: `IsComposition = false`, `DstId = null`
- When the impl is `unsafe impl`, the edge shall carry `is_unsafe: "true"` in props
- When a type has multiple trait impls (same or different files), each shall produce its own IMPLEMENTS edge

### EXTENDS Edges
- The materializer shall create EXTENDS edges from trait `rs.type` to each supertrait
- When a trait is `trait Read: BufRead + Seek`, EXTENDS edges shall be created to "BufRead" and "Seek"
- EXTENDS edges shall be reference edges: `IsComposition = false`, `DstId = null`
- EXTENDS edges shall carry `target` (supertrait name) in props

### DERIVES Edges
- The materializer shall create DERIVES edges from `rs.type` to each derived trait
- When `#[derive(Debug, Clone, Serialize)]` is on `struct Config`, three DERIVES edges shall be created
- DERIVES edges shall be reference edges: `IsComposition = false`, `DstId = null`
- DERIVES edges shall carry `target` (trait name) in props
- The `derives` string prop on `rs.type` (already set in Plan 02) shall remain as the quick-access path

### Derive Honesty Annotations
- When derive macros are applied to a type, the materializer shall create an `rs.macro_expansion` annotation
- The annotation shall have: `kind: "rs.macro_expansion"`, `rule_id` set to "derive", `message` listing the derived traits and noting generated impl blocks are not captured
- The annotation's `scope_document_id` shall reference the containing document node
- The annotation's `target_span_id` shall point to the derive attribute's span

### Updated rust_types View
- The `rust_types` view shall aggregate across files by `qualified_name`
- The view shall include `definition_count` (COUNT(DISTINCT doc.uri)) showing how many files define or extend the type
- The view shall include `defined_in` (LIST(DISTINCT doc.uri)) showing all files
- Stub nodes (`is_stub: true`) shall fold naturally into the aggregation — same `qualified_name` as the defining node

### SQL Views

**rust_impls** — trait implementations with target type and trait name:
- Shall show: `type_uri`, `target_type`, `target_qualified_name`, `trait_name`, `is_unsafe`, `document_uri`
- Shall join from IMPLEMENTS edge → source `rs.type` node → parent document via HAS_PART

**rust_derives** — derive macro relationships per type:
- Shall show: `type_uri`, `type_name`, `type_qualified_name`, `derived_trait`
- Shall join from DERIVES edge → source `rs.type` node

**rust_unsafe** — everything marked unsafe in one query:
- Shall UNION ALL across: unsafe functions (from rust_functions), unsafe methods (from rust_methods), unsafe traits (from rust_types where type_kind = 'trait'), unsafe trait impls (from rust_impls)
- Each row shall have: `item_kind` ("function"/"method"/"trait"/"impl"), `name`, `qualified_name`, `document_uri`

### Ordinal Correctness
- Methods from trait impl blocks shall carry ordinals relative to the impl block's position in the file, preserving source order
- When multiple impl blocks add methods to the same type, ordinals shall preserve intra-block order

## Constraints

- **Deferred reference pattern** — all reference edges (IMPLEMENTS, EXTENDS, DERIVES) use `DstId = null` with target name in `Props["target"]`. Resolution to actual node IDs happens in the multi-file analysis phase, not at parse time. This is the standard cross-format pattern
- **Stub nodes follow Ruby open class pattern** — each file that contains impl blocks for a type creates its own `rs.type` node. The `rust_types` view aggregates by `qualified_name`. Proven pattern from Ruby format
- **Stub `kind` defaults to `"struct"`** — when the target type's actual kind (struct/enum/union) is unknown at parse time, default to `"struct"`. Multi-file analysis can correct this. The shared Types view works regardless of the specific kind value
- **rust_unsafe depends on rust_functions, rust_methods, rust_types, rust_impls** — this view is a UNION ALL, not a base query. All constituent views must exist
- **Derive annotations are the only honesty surface in this plan** — non-derive macro invocation annotations are Plan 04's scope

## References

- [Rust Format Design](../../designs/future/rust-format.md) — Graph Materialization (Edges), Impl Block Resolution, Cross-Cutting Concerns (Scattered implementations, Deferred references)
- [Ruby Mixin Graph Plan](../ruby-format/03-mixin-graph.md) — reference for edge materialization and open class pattern
- [PHP Trait/Inheritance Edges](../../../src/Formats/RepoQL.Formats.PHP/PHPLoader.cs) — reference for EXTENDS/IMPLEMENTS deferred edge pattern
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Impl block target type name is a complex generic expression | Use simplified name (strip generics), log diagnostic |
| Trait name in impl is a path (`std::fmt::Display`) | Store full path as `target` — resolution handles paths |
| Supertrait expression too complex (lifetime bounds, HRTBs) | Store raw text as `target`, log diagnostic |
| Derive attribute parsing fails | Store raw attribute text in `derives` prop, skip DERIVES edges, log diagnostic |
| Multiple impl blocks for same type — conflicting kind | First definition wins for `kind` prop on stub node |
