---
description: Research into approaches for parsing Rust source code from .NET, informing Rust format support in RepoQL
tags: [rust, parsing, formats, tree-sitter, rust-analyzer, syn, antlr, cargo]
audience: { human: 50, agent: 50 }
purpose: { research: 90, reference: 10 }
---

# Parsing Rust from .NET

Research for the decision of how to add Rust format support to RepoQL — specifically, which parsing approach to use for extracting structure (structs, enums, traits, impls, functions, modules, macros, relationships) from Rust source files within a .NET/C# indexing pipeline.

*Research date: 2026-02-23*

## Context

RepoQL needs a Rust format loader following the same pattern as existing loaders. The loader must:

- Extract structural declarations: structs, enums, traits, impl blocks, functions, modules, macros, constants, statics, type aliases, unions
- Capture relationships: trait implementations, supertrait bounds, derive macros, use/import paths, module containment
- Produce nodes, edges, and spans for the knowledge graph
- Handle malformed files gracefully (errors never cascade)
- Run cross-platform (Windows, Linux, macOS)
- Avoid requiring Rust toolchain on the indexing machine (preferred, not mandatory)

**Existing format loader patterns in RepoQL:**

| Format | Parser | Integration | External dependency |
|--------|--------|-------------|-------------------|
| C# | Roslyn (Microsoft.CodeAnalysis) | NuGet, in-process | None |
| TypeScript | TS Compiler API | Node.js subprocess over stdin/stdout JSON | Node.js runtime |
| Ruby | tree-sitter (TreeSitter.DotNet) | NuGet, in-process | None |
| PHP | ANTLR4 (grammar compiled to C#) | NuGet, in-process | None |

**Rust-specific parsing challenges:**

- **Macros** generate code invisibly — `macro_rules!` and procedural macros define symbols that exist only after expansion
- **Impl blocks** are detached from type definitions — methods live in `impl Type { }` blocks that can appear anywhere, including other files
- **Generics** are pervasive — lifetime parameters (`'a`), type parameters, const generics, and where clauses create complex signatures
- **Conditional compilation** (`#[cfg(...)]`) means code may only exist under certain configurations
- **Module system** maps to filesystem — `mod config;` loads `config.rs` or `config/mod.rs`

---

## Tree-Sitter via TreeSitter.DotNet

The [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) NuGet package (v1.3.0 as of January 2026) provides .NET Standard 2.0 bindings to tree-sitter with 28+ bundled language grammars, **including Rust**. All native binaries for win-x64, win-x86, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 ship inside the single NuGet package.

> [TreeSitter.DotNet on NuGet](https://www.nuget.org/packages/TreeSitter.DotNet) — MIT license (stats as of 2026-02-23)

**RepoQL already uses this package** for the Ruby format loader. The integration pattern is proven: `RubyTreeSitterClient` demonstrates language creation, thread-local parsers, query-based extraction, and error node handling.

The underlying [tree-sitter-rust](https://github.com/tree-sitter/tree-sitter-rust) grammar is the canonical Rust grammar maintained under the official tree-sitter organization. Latest release: v0.24.0 (April 2025). MIT licensed. Widely adopted (~1,100 downstream dependents as of 2026-02-23).

> [tree-sitter-rust on GitHub](https://github.com/tree-sitter/tree-sitter-rust) — official grammar, used by Neovim, Helix, Zed, and GitHub code navigation

### Available AST node types

The grammar covers all top-level Rust constructs:

| Node type | Rust construct |
|-----------|---------------|
| `struct_item` | struct definitions |
| `enum_item` | enum definitions |
| `trait_item` | trait definitions |
| `impl_item` | impl blocks (inherent and trait) |
| `function_item` | free function definitions |
| `function_signature_item` | trait method signatures (no body) |
| `mod_item` | module definitions |
| `use_declaration` | import/use statements |
| `const_item` | constant declarations |
| `static_item` | static variables |
| `type_item` | type aliases |
| `macro_definition` | `macro_rules!` definitions |
| `macro_invocation` | macro calls |
| `attribute_item` | attributes including `#[derive(...)]` |
| `extern_crate_declaration` | extern crate imports |
| `foreign_mod_item` | FFI blocks (`extern "C" { }`) |
| `union_item` | union definitions |
| `enum_variant` | individual enum variants |
| `associated_type` | associated types in traits |
| `visibility_modifier` | `pub`, `pub(crate)`, `pub(super)`, `pub(in path)` |

> [tree-sitter-rust node-types.json](https://github.com/tree-sitter/tree-sitter-rust/blob/master/src/node-types.json) — full node type catalog

### Error tolerance

Tree-sitter is inherently error-tolerant. When it encounters a syntax error, it marks the problematic region with an `ERROR` node and continues parsing the rest of the file, producing valid AST nodes for non-erroneous portions. `MISSING` nodes are inserted for expected-but-absent tokens.

The Ruby loader already handles this pattern: counting error nodes via `CountErrorNodes(root)` and filtering `c.Node.IsError` in query results. Identical approach applies for Rust.

> [Tree-sitter error recovery design](https://tree-sitter.github.io/tree-sitter/) — always returns a usable tree

### Performance

- Initial parse of a typical source file: single-digit milliseconds (tree-sitter-rust README reports 2,157 lines in ~6.48ms)
- tree-sitter-rust parse speed roughly 2-3x slower than rustc's hand-written parser, which is still very fast
- Performance scales linearly with file size; incremental re-parsing after edits is sub-millisecond
- Native C library via P/Invoke — negligible interop overhead
- Thread-safe with `ThreadLocal<Parser>` pattern (already used in Ruby loader)

> [tree-sitter-rust README benchmarks](https://github.com/tree-sitter/tree-sitter-rust) — parse timing for sample files

### Macro handling

This is tree-sitter's primary limitation for Rust:

- **`macro_rules!` definitions** — parsed as `macro_definition` nodes. The body is captured but internal pattern/template structure is only partially parsed (macro DSLs are custom syntax)
- **Macro invocations** — captured as `macro_invocation` nodes with arguments tokenized but not semantically expanded
- **Proc macros** — invocations via `#[derive(...)]` and `#[attribute]` are captured as `attribute_item` nodes. Generated code is invisible (no macro expansion)
- **Complex generics** — well-supported syntactically. Nested angle brackets, lifetime parameters, where clauses, and associated type bounds all parse correctly

For RepoQL's use case (extracting the shape of code, not its semantic meaning after expansion), agents can see that `#[derive(Debug, Clone, Serialize)]` is applied to a struct, even though the generated `impl Debug` block is invisible. Whether this trade-off is acceptable depends on how much agent workflows rely on macro-expanded symbols.

### Integration effort

Low. A `RustTreeSitterClient` would follow the identical architecture as `RubyTreeSitterClient`:

```csharp
// Language creation (one-time, shared)
private static readonly Language SharedLanguage = new Language("tree-sitter-rust", "tree_sitter_rust");

// Thread-safe parsing
private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);

// Query-based extraction
using var query = SharedLanguage.CreateQuery("(struct_item name: (type_identifier) @name) @struct");
```

No new NuGet dependencies. No new external runtime. Same deployment model.

### Risks

- **TreeSitter.DotNet**: Single maintainer (mariusgreuel), 10 GitHub stars. Small project, single-point-of-failure risk. This is a shared risk with the Ruby loader — already accepted.
- **Grammar lag**: tree-sitter-rust typically lags rustc by weeks to months for new syntax. Most Rust 2024 edition features appear present in v0.24.0 (e.g., `gen_block` node type), but lag for future syntax additions is expected.
- **No semantic analysis**: Cannot resolve which `impl` block's methods apply to a given type across files. Cannot resolve `use` paths to concrete symbols. Cannot expand macros.

---

## rust-analyzer via LSP or as Library

[rust-analyzer](https://rust-analyzer.github.io/) is the de facto standard Rust language server, providing compiler-grade semantic analysis: type inference, name resolution, trait resolution, and macro expansion.

> [rust-analyzer manual](https://rust-analyzer.github.io/manual.html) — official documentation

### .NET integration paths

**Via LSP protocol**: Launch `rust-analyzer` as a subprocess, communicate via JSON-RPC over stdio. A .NET LSP client library handles protocol framing. Architecturally similar to how editors integrate.

**Via `ra_ap_syntax` crate**: The [ra_ap_syntax](https://docs.rs/ra_ap_syntax/latest/ra_ap_syntax/) crate extracts rust-analyzer's parser as a standalone Rust library. It provides error-tolerant, lossless CST parsing (inspired by Swift's libSyntax). Would require building a Rust binary that emits JSON — similar distribution complexity to the `syn` approach.

> [ra_ap_syntax docs](https://docs.rs/ra_ap_syntax/latest/ra_ap_syntax/) — standalone parser from rust-analyzer

### What it provides beyond syntax

- Full type inference across crate boundaries
- Name resolution — `use` paths resolved to concrete definitions
- Trait implementation resolution — knows which methods apply to which types
- Macro expansion (including proc macros, with limitations)
- `textDocument/documentSymbol` with full symbol hierarchy
- Cross-file go-to-definition and find-references

> [rust-analyzer Architecture](https://rust-analyzer.github.io/book/contributing/architecture.html) — layer descriptions: syntax tree → ItemTree → DefMap → HIR

### Resource consumption

This is a major concern:

| Resource | Reported usage | Source | Confidence |
|----------|---------------|--------|------------|
| Memory | 1–2.5+ GB RAM per project | [rust-analyzer #11325](https://github.com/rust-lang/rust-analyzer/issues/11325), [#13954](https://github.com/rust-lang/rust-analyzer/issues/13954) | Medium — issue reports, varies by project |
| CPU | ~20% usage even when idle | Anecdotal (GitHub issues, forum threads) | Low — no systematic benchmark found |
| Startup time | Seconds to minutes for initial project loading | Depends on project size | Medium — widely reported |

For RepoQL, which must "run on a developer laptop" alongside IDE, browser, and LLM client, adding rust-analyzer's memory footprint per indexed Rust project is problematic. A user with rust-analyzer already running for their IDE would effectively have two instances consuming 2–5 GB combined.

> [rust-analyzer memory usage issues](https://github.com/rust-lang/rust-analyzer/issues/11325) — community-reported memory concerns

### Cargo/Rustup requirements

**Effectively required.** rust-analyzer works best with a `Cargo.toml` project. Standalone `.rs` file support exists ([PR #8955](https://github.com/rust-lang/rust-analyzer/pull/8955)) but with significant limitations: no dependency resolution, reduced analysis features. For non-Cargo projects, a `rust-project.json` must be provided manually describing the project structure.

> [rust-analyzer: non-Cargo projects](https://rust-analyzer.github.io/book/non_cargo_based_projects.html) — manual project configuration

### Integration effort

High. Would require either:
1. An LSP client managing a long-running subprocess with complex lifecycle (start, initialize, sync, query, shutdown)
2. A custom Rust binary wrapping `ra_ap_syntax` with JSON serialization and cross-platform distribution

Neither matches RepoQL's existing format loader patterns.

### Risks

- Heavy resource footprint conflicts with "runs on a developer laptop" constraint
- Cargo requirement means files outside a Cargo project get degraded analysis
- Complex integration pattern — no existing RepoQL loader uses a persistent external server
- rust-analyzer's internal API is unstable; `ra_ap_*` crates have frequent breaking changes

---

## syn (Rust Parser Library)

[syn](https://github.com/dtolnay/syn) is the standard Rust parsing library, primarily designed for procedural macros. Maintained by David Tolnay, one of Rust's most prolific contributors. It provides a fully-typed AST covering all Rust syntax.

> [syn on crates.io](https://crates.io/crates/syn) — 350M+ downloads, MIT/Apache-2.0 dual license

### Integration approach

Would require building a small Rust CLI binary:

```rust
let file = syn::parse_file(&source_code)?;
let json = serde_json::to_string(&extract_symbols(&file))?;
println!("{}", json);
```

Shell out from .NET, parse JSON result. Batch files or use long-running subprocess with stdin/stdout to amortize startup cost.

### What it captures

Complete Rust AST:

- All items: structs, enums, traits, impls, functions, modules, type aliases, constants, statics, extern blocks, macros
- Full type expressions with generics, lifetimes, bounds
- Function signatures with all parameter detail
- Attribute metadata (including derives)
- Visibility modifiers
- Where clauses
- Doc comments

### Error tolerance

**syn is NOT error-tolerant.** It returns `Result<T, syn::Error>` and stops at the first parse error. No partial AST recovery. A single syntax error means zero output.

> [syn API docs](https://docs.rs/syn/latest/syn/) — parse functions return Result, no incremental recovery

This is a significant constraint mismatch. RepoQL promises "one bad file never breaks anything else." A parser that returns nothing on any syntax error produces zero graph data for that file.

### Distribution concerns

- Must distribute compiled Rust binaries for each target platform (Windows x64, Linux x64, macOS x64/arm64)
- Build requires Rust toolchain in CI
- Adds cross-compilation complexity
- Binary size: small (few MB) but still an additional artifact

### Performance

Fast for parsing (estimated sub-millisecond per file based on syn's design for proc-macro hot paths, though no published benchmarks were found). Process startup overhead is the bottleneck if shelling out per file — would need batching or a long-running subprocess.

### Risks

- **No error tolerance** — a single syntax error returns nothing, conflicting with RepoQL's error isolation constraint
- Distribution complexity of a separate Rust binary
- New integration pattern not used by any existing loader
- Maintenance burden of a custom Rust bridge binary

---

## ANTLR4 Rust Grammar

The [antlr/grammars-v4](https://github.com/antlr/grammars-v4) repository contains a Rust grammar with C# target support.

> [ANTLR Rust grammar](https://github.com/antlr/grammars-v4/tree/master/rust) — community-maintained grammar

### Quality and completeness

**Significantly dated.** The grammar:

- Last updated for **Rust v1.60.0** (released April 2022)
- Only implements v2018+ stable features
- Missing Rust 2021 edition features: `let-else`, inline const, GATs
- Missing Rust 2024 edition features entirely
- Based on the official language reference, so core syntax is reasonable

> [ANTLR Rust grammar README](https://github.com/antlr/grammars-v4/blob/master/rust/README.md) — states v1.60.0 target

Rust 1.60 to current stable (1.85+ as of February 2026) represents ~25 releases of language evolution. Missing features include:

| Feature | Rust version | Status in grammar |
|---------|-------------|-------------------|
| `let-else` | 1.65 | Missing |
| Generic associated types (GATs) | 1.65 | Missing |
| `async fn` in traits | 1.75 | Missing |
| Return-position `impl Trait` in traits | 1.75 | Missing |
| `#[diagnostic]` namespace | 1.78 | Missing |
| `unsafe extern` blocks | 1.82 | Missing |

### Integration

ANTLR's C# target is well-supported. RepoQL already has ANTLR infrastructure in the `RepoQL.Grammar` project. The PHP loader uses ANTLR4 via compiled grammar. Integration would follow the same pattern.

### Error tolerance

ANTLR4 has built-in error recovery (`DefaultErrorStrategy`) — can skip tokens and insert missing ones. Quality depends heavily on grammar structure. For complex grammars, error recovery often produces large ERROR regions rather than graceful degradation.

### Risks

- **Grammar is 3+ years behind current Rust.** Maintaining a fork is substantial ongoing work
- No active maintainers — grammar updates come from community PRs with unpredictable cadence
- tree-sitter-rust, maintained by a large community and used in every major editor, is far more current

---

## Cargo Metadata and Rustdoc JSON

Two supplementary data sources that provide information syntax parsers cannot:

### cargo metadata

The [`cargo metadata`](https://doc.rust-lang.org/cargo/commands/cargo-metadata.html) command outputs JSON describing:

- All packages in the workspace with names, versions, editions
- Full dependency graph (resolved and unresolved)
- Target information (lib, bin, example, test, bench)
- Feature flag definitions and activation
- Source file paths
- License and repository metadata

> [cargo metadata docs](https://doc.rust-lang.org/cargo/commands/cargo-metadata.html) — machine-readable project structure

**Requires `cargo` to be installed.** Output is project-level metadata, not file-level syntax. Useful for enriching the knowledge graph with dependency edges and crate structure.

### cargo rustdoc --output-format json

The [rustdoc JSON format](https://rust-lang.github.io/rfcs/2963-rustdoc-json.html) provides machine-readable API documentation:

- All public items with full type signatures
- Source locations (file + line/column spans)
- Impl blocks and trait implementations
- Documentation strings
- Deprecation information
- ~25+ item types represented

> [rustdoc JSON RFC 2963](https://rust-lang.github.io/rfcs/2963-rustdoc-json.html) — format specification

**Critical limitations:**
- **Nightly-only** (`-Z unstable-options` required) — not available on stable rustc
- Exports **public** items by default (private items require explicit rustdoc options)
- Requires a valid, compilable Cargo project
- Takes seconds to minutes (full compilation required)
- Cannot handle partial or broken files

### Integration as supplementary source

These tools provide information tree-sitter cannot: resolved dependency graphs, workspace structure, and cross-crate type information. They could serve as optional enrichment when the Rust toolchain is available, without being required for basic Rust file indexing.

---

## Other Approaches Considered

### ast-grep

[ast-grep](https://github.com/ast-grep/ast-grep) is a CLI tool built on tree-sitter providing structural code search with JSON output. Supports Rust. However, it adds a binary dependency and is less flexible than direct tree-sitter integration. Since RepoQL already has TreeSitter.DotNet, direct tree-sitter integration avoids the additional dependency.

> [ast-grep on GitHub](https://github.com/ast-grep/ast-grep) — structural search tool

### ra_ap_syntax standalone

The `ra_ap_syntax` crate extracts rust-analyzer's parser as a standalone library with full error tolerance (any text produces a syntax tree). Would require building a Rust bridge binary — same distribution complexity as `syn` but with genuine error tolerance.

Worth considering as a future upgrade if tree-sitter's error recovery proves insufficient. However, `ra_ap_*` crates have frequent breaking changes and the integration complexity is higher than tree-sitter.

> [ra_ap_syntax API](https://docs.rs/ra_ap_syntax/latest/ra_ap_syntax/) — error-tolerant, lossless CST

### csbindgen (Rust-to-C# FFI)

[csbindgen](https://github.com/Cysharp/csbindgen) generates C# P/Invoke bindings from Rust `extern "C"` functions. Could wrap `ra_ap_syntax` or `syn` as a native DLL callable from C#. Best-of-both-worlds potential but significant development and maintenance cost.

> [csbindgen on GitHub](https://github.com/Cysharp/csbindgen) — Rust-to-C# binding generator

---

## Comparison

| Dimension | Tree-sitter | rust-analyzer | syn | ANTLR4 |
|-----------|-------------|---------------|-----|--------|
| .NET integration | NuGet, in-process (existing) | Subprocess + LSP or custom binary | Custom Rust binary | NuGet, in-process (existing) |
| Error tolerance | Yes (ERROR/MISSING nodes) | Yes (lossless CST) | **No** (fails on first error) | Partial (large error regions) |
| External dependency | None | Rust toolchain + cargo | Rust binary per platform | None |
| Symbols extracted | All syntax-level constructs | All + type inference, name resolution, macro expansion | All syntax-level | Core syntax (dated to Rust 1.60) |
| Performance | Sub-10ms per file | Seconds startup, heavy ongoing | Sub-ms + process overhead (est.) | Comparable to tree-sitter (est.) |
| Memory | Minimal (parse and discard) | 1–2.5+ GB per project | Minimal per invocation | Minimal |
| Handles standalone .rs | Yes | Poorly | Yes | Yes |
| Macro handling | Captures invocations, no expansion | Expands (with limitations) | Full syntax, no expansion | Limited |
| Grammar currency | Active, weeks behind rustc | Tracks nightly | Tracks stable | 3+ years behind |
| Existing RepoQL pattern | Identical to Ruby loader | New integration pattern | New integration pattern | Similar to PHP loader |
| Deployment | Zero config | Requires Rust toolchain | Must ship cross-platform binaries | Zero config |

### What each approach enables that others don't

| Capability | Only available from |
|------------|-------------------|
| Macro-expanded code | rust-analyzer |
| Cross-file name resolution | rust-analyzer |
| Type inference | rust-analyzer |
| Dependency graph | cargo metadata |
| Zero-dependency indexing | tree-sitter, ANTLR |
| Error-tolerant parsing | tree-sitter, rust-analyzer, ra_ap_syntax |

---

## Rust Language Constructs to Capture

Regardless of parser choice, a Rust format loader should extract these constructs, organized by implementation priority:

### Tier 1 — Structural skeleton

Enables "what's in this file?" queries.

| Construct | Suggested `node.kind` | Key properties |
|-----------|-----------------------|----------------|
| Free function | `rust.function` | `name`, `visibility`, `is_async`, `is_const`, `is_unsafe`, `return_type`, `parameters` |
| Struct | `rust.type` (`type_kind=struct`) | `name`, `visibility`, `generics`, `derives` |
| Enum | `rust.type` (`type_kind=enum`) | `name`, `visibility`, `generics`, `derives` |
| Enum variant | `rust.member` (`kind=enum_variant`) | `name`, `variant_kind` (unit/tuple/struct) |
| Trait | `rust.type` (`type_kind=trait`) | `name`, `visibility`, `is_auto`, `is_unsafe`, `supertraits` |
| Impl block | Not a node — members parent to target type | `trait_name` (if trait impl), `target_type` |
| Method | `rust.member` (`kind=method`) | `name`, `visibility`, `is_async`, `is_unsafe`, `self_kind`, `return_type` |
| Module | `rust.module` | `name`, `visibility`, `is_inline` |
| Constant | `rust.constant` | `name`, `visibility`, `const_type` |
| Static | `rust.static` | `name`, `visibility`, `static_type`, `is_mutable` |
| Type alias | `rust.type` (`type_kind=type_alias`) | `name`, `visibility`, `aliased_type` |
| Trait alias | `rust.type` (`type_kind=trait_alias`) | `name`, `visibility`, `bounds` |
| Union | `rust.type` (`type_kind=union`) | `name`, `visibility`, `generics` |
| Field | `rust.member` (`kind=field`) | `name`, `visibility`, `field_type` |
| Associated const | `rust.member` (`kind=associated_const`) | `name`, `const_type`, `has_default` |
| Associated type | `rust.member` (`kind=associated_type`) | `name`, `bounds`, `default_type` |
| Macro definition | `rust.macro` | `name`, `visibility`, `macro_kind` (declarative/proc) |
| Extern function | `rust.member` (`kind=extern_fn`) | `name`, `abi`, `parameters`, `return_type` |

### Tier 2 — Relationships

Enables "how do things connect?" queries.

| Edge type | From → To | Notes |
|-----------|-----------|-------|
| `HAS_PART` | module → items, type → members | Composition (tree structure) |
| `IMPLEMENTS` | type → trait | From `impl Trait for Type` blocks |
| `EXTENDS` | trait → trait | Supertrait bounds |
| `DERIVES` | type → trait | From `#[derive(...)]` attributes |
| `IMPORTS` | module → symbol | `use` declarations. Props: `alias`, `is_glob` |
| `RE_EXPORTS` | module → symbol | `pub use` |
| `USES_SYMBOL` | any → any | General symbol reference |

### Tier 3 — Rich properties

Enables detailed queries about signatures and qualifiers.

- Generics and where clauses (serialized as JSON properties)
- Full visibility variants (`pub`, `pub(crate)`, `pub(super)`, `pub(in path)`)
- `cfg` predicates on conditionally-compiled items
- Unsafe markers on functions, traits, impl edges
- ABI strings on extern functions and blocks
- Lifetime parameters

### Tier 4 — Ecosystem integration

Requires Rust toolchain on target machine.

- Cargo.toml parsing (crate metadata, dependencies, features, workspace structure)
- `DEPENDS_ON` edges between crates
- Feature flag definitions as annotations

---

## Gaps

- **Macro expansion**: No syntax-only parser can see macro-generated code. The `#[derive(Serialize)]` attribute on a struct implies an `impl Serialize` block exists, but without expansion, the specific implementation details are invisible. How much this matters for agent workflows is unclear — agents typically care about the derive list itself rather than the generated code.

- **Cross-file impl resolution**: Rust allows `impl` blocks for a type to appear in any file within the same crate. A syntax parser can see the impl block and its target type name, but cannot resolve which type definition it refers to across files. This would require either name resolution (rust-analyzer) or a post-parsing analysis pass using heuristics (matching qualified names).

- **Edition detection**: Rust editions (2015, 2018, 2021, 2024) change syntax rules. tree-sitter-rust targets the latest edition. Whether older edition syntax causes parse errors is not documented in the grammar's README. Files in edition-2015 crates using deprecated patterns may parse differently.

- **Conditional compilation coverage**: Indexing all `#[cfg(...)]` branches (rather than a single active configuration) gives complete coverage but may include items that cannot coexist at runtime. Whether this confuses agents in practice is unknown.

- **Proc macro definitions**: Proc macros are defined in separate crates using the `proc_macro` API. Their definitions look like normal functions to a syntax parser — the fact that they transform syntax is not visible without understanding the `proc_macro` crate dependency.

- **Module path resolution**: `mod foo;` declarations reference files (`foo.rs` or `foo/mod.rs`), but resolving this mapping requires knowledge of the crate root and directory structure. A syntax parser sees the declaration but not the target file. A post-parsing pass could resolve these using filesystem conventions.

- **`include!` and build-script generated code**: `include!("generated.rs")` and code generated by `build.rs` scripts are invisible to syntax parsers. These patterns are common in protobuf, FFI bindings, and code generation workflows.

- **Re-export and name alias disambiguation**: `pub use foo::Bar as Baz;` creates a new name for an existing symbol. Tracking these aliases without name resolution is possible syntactically but resolving what `Bar` refers to across modules is not.

- **TreeSitter.DotNet stability**: Single maintainer, small project. If the package becomes unmaintained, RepoQL would need to either fork it or migrate to another tree-sitter binding. This risk is shared with the Ruby loader — already accepted but worth noting.

- **Validation**: No corpus-based testing has been performed. Parse success rates and error recovery quality across Rust editions, real-world crates, and edge cases (deeply nested generics, complex macro invocations) are untested.

---

## Summary

| Approach | Integration cost | Constraint fit | Key trade-off |
|----------|-----------------|----------------|---------------|
| Tree-sitter (TreeSitter.DotNet) | Low — reuses existing NuGet + Ruby loader pattern | Matches all constraints | No semantic analysis, no macro expansion |
| rust-analyzer (LSP) | High — new integration pattern, persistent subprocess | Conflicts with laptop resource constraint (1–2.5 GB RAM) | Full semantic analysis at high resource cost |
| syn (Rust binary) | Medium — custom Rust binary + cross-platform distribution | Conflicts with error isolation constraint (no partial recovery) | Complete AST but zero output on any syntax error |
| ANTLR4 | Low — existing ANTLR infrastructure | Grammar 3+ years behind current Rust | Ongoing grammar maintenance burden |
| cargo metadata | Low — subprocess + JSON parsing | Requires Rust toolchain | Project-level only, not file-level syntax |
| ra_ap_syntax | High — custom Rust binary + FFI or subprocess | Error-tolerant, rich AST | Distribution complexity, unstable API |

Tree-sitter reuses existing infrastructure, adds zero new dependencies, and covers all syntax-level constructs. rust-analyzer provides capabilities no other option can (macro expansion, type inference, cross-file resolution) but at significantly higher integration and resource cost. `cargo metadata` is orthogonal and could supplement any syntax parser.
