---
description: Plan for Rust format — use declaration import edges, macro_rules! definitions, macro invocation annotations, proc-macro attribute annotations, and honesty surface views
tags: [format, rust, plan, imports, macros, honesty, annotations]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Rust — Imports, Macros, and Honesty

Implements: [Rust Format Design](../../designs/future/rust-format.md) — Graph Materialization (IMPORTS edges, rs.macro nodes), Macros & Attributes (macro_rules!, invocations, proc-macro attributes), SQL Views (rust_imports, rust_macros, rust_macro_expansion)

## Scope

**Covers:**
- IMPORTS edges from document to symbol paths (from `use` declarations)
- `rs.macro` nodes for `macro_rules!` definitions
- `rs.macro_expansion` annotations for non-derive macro invocations (top-level and nested)
- `rs.macro_expansion` annotations for proc-macro attributes (`#[tokio::main]`, `#[async_trait]`, etc.)
- Structured attribute extraction for key attributes (`#[test]`, `#[cfg()]`, `#[inline]`, `#[must_use]`, `#[deprecated]`)
- SQL views: `rust_imports`, `rust_macros`, `rust_macro_expansion`
- Tests: import edge creation, glob imports, re-exports, macro definitions, macro invocations, attribute extraction, honesty annotations

**Does not cover:**
- Require path resolution to actual file URIs (multi-file analysis phase)
- Module-to-file path resolution (multi-file analysis phase — parser captures the declaration, resolution is cross-file)
- Cargo.toml parsing (extension point — separate TOML loader)
- Feature flag conditional compilation queries (extension point — `#[cfg]` predicates are captured as properties in this plan, but feature-flag-specific views are an extension)

## Enables

Once this exists:
- **Import graph is queryable** — `SELECT * FROM rust_imports WHERE import_path LIKE 'std::collections%'` finds every file that uses stdlib collections
- **Re-exports visible** — `SELECT * FROM rust_imports WHERE is_reexport` shows the crate's public API surface
- **Glob imports detectable** — `SELECT * FROM rust_imports WHERE is_glob` finds `use foo::*` patterns
- **Macros are discoverable** — `SELECT * FROM rust_macros` lists all macro_rules! definitions with their locations
- **Honesty surface complete** — `SELECT * FROM rust_macro_expansion` shows every point where macro expansion makes the graph incomplete — derives, proc-macro attributes, and macro invocations
- **Agents know what's invisible** — the graph is honest. No silent gaps. An agent querying `rust_macro_expansion` for a file sees exactly where generated code exists that the graph doesn't capture

This is the honesty and completeness increment. After this, the Rust format captures everything syntactically visible and marks everything that isn't.

## Prerequisites

- Plan 02 complete — document nodes exist for import edges, materialization pipeline operational
- Plan 03 recommended but not required — derive annotations (Plan 03) and non-derive macro annotations (this plan) are independent. Both contribute to `rust_macro_expansion` view. If Plan 04 is built before Plan 03, the view will show non-derive annotations only until Plan 03 adds derive annotations
- `RustTreeSitterClient` already extracts use declarations, macro_rules! definitions, macro invocations, and attributes (Plan 01 queries)

## North Star

An agent should see every `use` path — what's imported, from where, whether it's re-exported. An agent should find every macro definition. And when macros or attributes generate invisible code, the agent should know exactly where and why the graph is incomplete. Honesty is a feature, not an apology.

## Done Criteria

### IMPORTS Edges
- The materializer shall create IMPORTS edges from the document node for each `use` declaration
- IMPORTS edges shall carry: `path` (the use path, e.g., `std::collections::HashMap`), `alias` (from `as` clause, null if absent), `is_glob` ("true" for `use foo::*`, "false" otherwise), `is_pub` ("true" for `pub use`, "false" otherwise)
- IMPORTS edges shall be reference edges: `IsComposition = false`, `DstId = null`
- When a use declaration has a tree (`use std::{io, fs}`), it shall be expanded into separate IMPORTS edges for each path
- When a use declaration has nested trees (`use std::{io::{self, Read}, fs}`), each leaf path shall produce its own edge

### rs.macro Nodes
- The materializer shall create `rs.macro` nodes for `macro_rules!` definitions
- `rs.macro` nodes shall have props: `name`, `qualified_name`, `accessibility`
- `rs.macro` nodes shall parent to the document (or enclosing module if inline) via HAS_PART composition edge
- The macro body is not structurally parsed — the node captures the definition's location and visibility

### Macro Invocation Annotations
- When a macro invocation is encountered (e.g., `lazy_static! { ... }`, `println!()`, `vec![]`), the materializer shall create an `rs.macro_expansion` annotation
- The annotation shall have: `kind: "rs.macro_expansion"`, `rule_id` set to the macro name (e.g., "lazy_static"), `message` describing that the macro expansion is not captured
- Top-level invocations shall have `scope_document_id` referencing the document
- Invocations within a type or function body shall have `target_span_id` pointing to the invocation's span
- Common macros that produce no structural output (`println!`, `dbg!`, `assert!`, `todo!`, `unimplemented!`, `panic!`, `format!`, `write!`, `writeln!`, `log::info!` and similar) shall NOT produce annotations — they don't create invisible structure
- The annotation is for macros that plausibly generate types, methods, or impl blocks: `lazy_static!`, `bitflags!`, custom proc-macros, etc.

### Proc-Macro Attribute Annotations
- When a proc-macro attribute is encountered (e.g., `#[tokio::main]`, `#[async_trait]`, `#[derive_more::Display]`), the materializer shall create an `rs.macro_expansion` annotation
- The annotation shall have: `rule_id` set to the attribute name, `message` describing the proc-macro invocation
- Built-in attributes that don't generate code (`#[allow]`, `#[deny]`, `#[warn]`, `#[cfg]`, `#[cfg_attr]`, `#[inline]`, `#[must_use]`, `#[deprecated]`, `#[doc]`, `#[repr]`, `#[path]`, `#[link]`, `#[no_mangle]`, `#[export_name]`) shall NOT produce annotations
- The distinction: does this attribute plausibly generate new code? If yes, annotate. If it only modifies compiler behavior, don't

### Structured Attribute Properties
- `#[test]` shall set `is_test: "true"` on the function node (already captured in Plan 02 surface model)
- `#[cfg(...)]` shall set `cfg` prop with predicate text on the decorated item
- `#[inline]` / `#[inline(always)]` shall set `is_inline` prop
- `#[must_use]` shall set `must_use` prop
- `#[deprecated]` shall set `is_deprecated` prop
- All attributes (including unrecognized ones) shall be stored in an `attributes` JSON array prop on the decorated item

### SQL Views

**rust_imports** — use declarations with alias and glob tracking:
- Shall show: `file_uri`, `import_path`, `alias`, `is_glob`, `is_reexport`
- Shall join from IMPORTS edge → source document node

**rust_macros** — macro_rules! definitions:
- Shall show: `file_uri`, `macro_uri`, `name`, `qualified_name`, `visibility`
- Shall join from rs.macro node → parent document via HAS_PART

**rust_macro_expansion** — honesty annotations about invisible macro-generated code:
- Shall show: `file_uri`, `macro_name` (from `rule_id`), `description` (from `message`), `line` (from span)
- Shall join from `rs.macro_expansion` annotations → document → optional span
- This view surfaces all macro expansion annotations: derives (from Plan 03), macro invocations (this plan), and proc-macro attributes (this plan)

### Filtering Noise
- The implementation shall maintain a set of known non-structural macros (the `println!` family, assertions, formatting) that are excluded from annotation
- The implementation shall maintain a set of known non-generative built-in attributes that are excluded from annotation
- Both sets shall be defined as constants in `RustConstants` for easy maintenance

## Constraints

- **Import path is the raw use path, not resolved** — `use std::collections::HashMap` stores `"std::collections::HashMap"`. Resolution to actual module locations is a multi-file analysis concern
- **Use tree expansion is syntactic** — `use std::{io, fs}` becomes two edges: `std::io` and `std::fs`. This is straightforward tree-sitter extraction from the `use_declaration` node's tree structure
- **Annotation filtering is conservative** — when uncertain whether a macro generates structure, annotate. False annotations are a minor cost. Missed annotations leave silent gaps in the graph
- **Built-in vs proc-macro distinction is heuristic** — the parser doesn't know which attributes are proc-macros. The constant set of known built-in attributes is the exclude list; everything else is treated as potentially generative
- **`rs.macro_expansion` is the unified annotation kind** — derives, macro invocations, and proc-macro attributes all use the same annotation kind. The `rule_id` distinguishes them. The `rust_macro_expansion` view surfaces all of them in one query

## References

- [Rust Format Design](../../designs/future/rust-format.md) — Macros & Attributes, Graph Materialization (IMPORTS, rs.macro), SQL Views (rust_imports, rust_macros, rust_macro_expansion)
- [Ruby Namespace Graph Plan](../ruby-format/04-namespace-graph.md) — reference for REQUIRES edge pattern (analogous to IMPORTS)
- [Ruby Metaprogramming Plan](../ruby-format/05-metaprogramming.md) — reference for honesty annotation pattern (`ruby.metaprogramming` ↔ `rs.macro_expansion`)
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Use declaration has complex path (macro-generated) | Skip edge, log diagnostic |
| Use tree expansion fails (deeply nested) | Store raw path text, skip expansion, log diagnostic |
| Attribute argument parsing fails | Store raw attribute text in `attributes` prop, still create annotation |
| Macro invocation name is a complex expression | Store raw text as `rule_id`, create annotation |
| Unknown attribute — can't determine if proc-macro | Annotate conservatively — false annotations are cheap |
