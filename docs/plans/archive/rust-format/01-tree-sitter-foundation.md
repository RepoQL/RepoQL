---
description: Plan for Rust format — project scaffolding, TreeSitter.DotNet integration, query-based extraction, and thread-safety validation
tags: [format, rust, tree-sitter, plan, parser]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Rust — Tree-Sitter Foundation

Implements: [Rust Format Design](../../designs/future/rust-format.md) — Tree-Sitter Integration, Project Structure

## Scope

**Covers:**
- New project `RepoQL.Formats.Rust` with TreeSitter.DotNet dependency
- New test project `RepoQL.Formats.Rust.Tests`
- `RustTreeSitterClient` — thread-safe wrapper containing all tree-sitter interop
- `RustQueries` — S-expression query strings for structural extraction
- Surface model types for parse results (no tree-sitter types escape)
- Thread-safety validation via concurrent parsing tests
- Solution file and `Directory.Packages.props` updates

**Does not cover:**
- Classification or media types (Plan: 02-core-format-loader)
- Materialization to graph nodes/edges (Plan: 02-core-format-loader)
- DI registration or pipeline integration (Plan: 02-core-format-loader)
- SQL views (Plans: 02 through 04)

## Enables

Once this exists:
- **Risk is retired** — TreeSitter.DotNet works on our platform, loads the Rust grammar, parses real files, handles errors, and is thread-safe. The single riskiest technical choice is validated before any downstream code is written
- **Plan 02 can proceed** — the core format loader consumes `RustTreeSitterClient` directly
- **Query coverage is known** — the S-expression queries are tested against real Rust patterns, so Plan 02 can build the surface model with confidence about what the parser delivers

This is the risk-retirement increment. Every subsequent plan assumes tree-sitter works.

## Prerequisites

- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — NuGet package, MIT license. Bundles tree-sitter-rust grammar and native binaries for win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64. Add to `Directory.Packages.props`
- .NET 10 SDK (solution already targets this)

## North Star

Parse any Rust file. Get structural elements back. Never crash, never leak tree-sitter types, never block another thread. When the file is broken, get partial results and a diagnostic — never nothing.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Rust` shall build targeting .NET 10
- The project shall reference `TreeSitter.DotNet`, `RepoQL.Contracts`, and `RepoQL.Indexing`
- The test project `RepoQL.Formats.Rust.Tests` shall reference TUnit, AwesomeAssertions, and FakeItEasy
- Both projects shall be included in `RepoQL.sln`
- `TreeSitter.DotNet` shall already be in `Directory.Packages.props` (added for Ruby format)

### RustTreeSitterClient
- The client shall accept Rust source code as a string and return a parse result
- The client shall use `ThreadLocal<Parser>` so each thread gets its own parser instance
- The `Language` object shall be created once and shared across all threads (`tree_sitter_rust` grammar)
- No tree-sitter types (`TSNode`, `TSTree`, `TSParser`) shall appear in the client's public API
- When source is empty, the client shall return an empty result (no exception)
- When source is null, the client shall throw `ArgumentNullException`

### Query-Based Extraction
- The client shall extract struct declarations with name, generics, where_clause, and field list
- The client shall extract enum declarations with name, generics, and variant list (unit/tuple/struct variants)
- The client shall extract trait declarations with name, generics, supertraits, and body (methods, associated types, associated consts)
- The client shall extract impl blocks with target_type, optional trait_name, generics, where_clause, is_unsafe, and method list
- The client shall extract function declarations (free functions) with name, parameters, return_type, is_async, is_unsafe, is_const
- The client shall extract function signatures (trait method declarations without body)
- The client shall extract module declarations with name and optional inline body
- The client shall extract use declarations with path, optional alias, and glob detection
- The client shall extract constant definitions with name and type
- The client shall extract static definitions with name, type, and is_mutable
- The client shall extract type alias definitions with name and aliased type
- The client shall extract union definitions with name and field list
- The client shall extract macro_rules! definitions with name
- The client shall extract macro invocations with macro name and byte range
- The client shall extract attributes (including derive) with name, arguments, and target span
- The client shall extract visibility modifiers (pub, pub(crate), pub(super), pub(in path))
- The client shall extract extern blocks with ABI string and function declarations
- The client shall extract doc comments (`///` and `//!`) as text associated with their target item

### Extensible Query Execution
- The client shall expose a method to execute additional S-expression queries against a parsed tree and return matched captures with byte ranges
- This enables downstream plans to add extraction patterns without modifying the client's core query set
- The method shall accept a query string and return a list of capture groups with names and byte ranges

### Error Recovery
- When source contains syntax errors, the client shall return results for valid regions and skip `ERROR` nodes
- The client shall report the count of `ERROR` nodes encountered
- When the tree-sitter native library fails to load, the error message shall name the package and platform

### Thread Safety
- When 8 threads parse different Rust files concurrently, all shall produce correct results
- When 8 threads parse the same Rust source concurrently, all shall produce identical results
- No thread shall receive another thread's parser state

### Surface Model Types
- Parse results shall use plain C# records/classes (no tree-sitter dependency)
- Each extracted element shall carry source byte range (start byte, end byte) for span creation
- The surface model types shall live in a `Surface/` subdirectory
- The surface model shall include all types listed in the design's `RustDocumentSurface` definition: `RustStructInfo`, `RustEnumInfo`, `RustEnumVariantInfo`, `RustTraitInfo`, `RustImplBlockInfo`, `RustMethodInfo`, `RustFieldInfo`, `RustFunctionInfo`, `RustModuleInfo`, `RustConstantInfo`, `RustStaticInfo`, `RustTypeAliasInfo`, `RustUnionInfo`, `RustMacroDefInfo`, `RustMacroInvocationInfo`, `RustUseDeclarationInfo`, `RustAttributeInfo`, `RustExternBlockInfo`, `RustByteRange`, `RustParseStats`

## Constraints

- **Containment boundary** — all tree-sitter interop is in `RustTreeSitterClient` and `RustQueries`. No other class in the project may reference TreeSitter.DotNet types. This enables swapping to ra_ap_syntax later without touching consumers
- **Query strings, not CST traversal** — use tree-sitter's S-expression query language for extraction. The design chose this over visitor pattern for robustness to grammar evolution
- **No materialization** — this plan validates the parser only. The client returns surface model types; conversion to graph nodes is Plan 02's scope
- **Follow Ruby client pattern** — mirror `RubyTreeSitterClient` structure for consistency across tree-sitter-based loaders

## References

- [Rust Format Design](../../designs/future/rust-format.md) — Tree-Sitter Integration section, S-expression queries, thread safety approach
- [Rust Parsing Research](../../research/rust-parsing-from-dotnet.md) — TreeSitter.DotNet evaluation, platform coverage, risk assessment
- [Ruby Tree-Sitter Client](../../../src/Formats/RepoQL.Formats.Ruby/TreeSitter/) — reference implementation for TreeSitter.DotNet integration pattern
- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — NuGet package with bundled Rust grammar
- [tree-sitter query syntax](https://tree-sitter.github.io/tree-sitter/using-parsers/queries) — S-expression pattern language
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — project structure conventions
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Parse errors produce partial results, never exceptions. The client catches all tree-sitter exceptions and translates them to diagnostics on the result object.

Native library loading failure at startup is the one hard error — if the grammar can't load, there's nothing to recover. The error message must be actionable: name the NuGet package, the expected RID, and suggest `dotnet restore`.
