---
description: Plan for Ruby format — project scaffolding, TreeSitter.DotNet integration, query-based extraction, and thread-safety validation
tags: [format, ruby, tree-sitter, plan, parser]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Ruby — Tree-Sitter Foundation

Implements: [Ruby Format Design](../../designs/current/ruby-format.md) — Tree-Sitter Integration, Project Structure

## Scope

**Covers:**
- New project `RepoQL.Formats.Ruby` with TreeSitter.DotNet dependency
- New test project `RepoQL.Formats.Ruby.Tests`
- `RubyTreeSitterClient` — thread-safe wrapper containing all tree-sitter interop
- `RubyQueries` — S-expression query strings for structural extraction
- Surface model types for parse results (no tree-sitter types escape)
- Thread-safety validation via concurrent parsing tests
- Solution file and `Directory.Packages.props` updates

**Does not cover:**
- Classification or media types (Plan: 02-core-format-loader)
- Materialization to graph nodes/edges (Plan: 02-core-format-loader)
- DI registration or pipeline integration (Plan: 02-core-format-loader)
- SQL views (Plans: 02 through 05)

## Enables

Once this exists:
- **Risk is retired** — TreeSitter.DotNet works on our platform, loads the Ruby grammar, parses real files, handles errors, and is thread-safe. The single riskiest technical choice is validated before any downstream code is written
- **Plan 02 can proceed** — the core format loader consumes `RubyTreeSitterClient` directly
- **Query coverage is known** — the S-expression queries are tested against real Ruby patterns, so Plan 02 can build the surface model with confidence about what the parser delivers

This is the risk-retirement increment. Every subsequent plan assumes tree-sitter works.

## Prerequisites

- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — NuGet package, MIT license. Bundles tree-sitter-ruby grammar and native binaries for win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64. Add to `Directory.Packages.props`
- .NET 10 SDK (solution already targets this)

## North Star

Parse any Ruby file. Get structural elements back. Never crash, never leak tree-sitter types, never block another thread. When the file is broken, get partial results and a diagnostic — never nothing.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Ruby` shall build targeting .NET 10
- The project shall reference `TreeSitter.DotNet`, `RepoQL.Contracts`, and `RepoQL.Indexing`
- The test project `RepoQL.Formats.Ruby.Tests` shall reference TUnit, AwesomeAssertions, and FakeItEasy
- Both projects shall be included in `RepoQL.sln`
- `TreeSitter.DotNet` shall be added to `Directory.Packages.props`

### RubyTreeSitterClient
- The client shall accept Ruby source code as a string and return a parse result
- The client shall use `ThreadLocal<Parser>` so each thread gets its own parser instance
- The `Language` object shall be created once and shared across all threads
- No tree-sitter types (`TSNode`, `TSTree`, `TSParser`) shall appear in the client's public API
- When source is empty, the client shall return an empty result (no exception)
- When source is null, the client shall throw `ArgumentNullException`

### Query-Based Extraction
- The client shall extract class declarations with name and optional superclass
- The client shall extract module declarations with name
- The client shall extract method declarations with name and parameter text
- The client shall extract singleton method declarations with receiver and name
- The client shall extract singleton class blocks (`class << self`)
- The client shall extract visibility modifier calls (bare and method-targeted)
- The client shall extract `attr_reader`, `attr_writer`, `attr_accessor` calls with argument names
- The client shall extract `include`, `extend`, `prepend` calls with module name
- The client shall extract constant assignments with name
- The client shall extract `require` and `require_relative` calls with path
- The client shall detect `yield` and block parameters within method bodies
- The client shall detect `define_method`, `class_eval`, `module_eval`, `instance_eval` calls
- The client shall extract `alias` statements with new_name and original_name
- The client shall extract `alias_method` calls with new_name and original_name

### Extensible Query Execution
- The client shall expose a method to execute additional S-expression queries against a parsed tree and return matched captures with byte ranges
- This enables downstream plans to add extraction patterns for framework-specific calls (`delegate`, `scope`, `has_many`, `validates`, `before_action`, etc.) without modifying the client's core query set
- The method shall accept a query string and return a list of capture groups with names and byte ranges

### Error Recovery
- When source contains syntax errors, the client shall return results for valid regions and skip `ERROR` nodes
- The client shall report the count of `ERROR` nodes encountered
- When the tree-sitter native library fails to load, the error message shall name the package and platform

### Thread Safety
- When 8 threads parse different Ruby files concurrently, all shall produce correct results
- When 8 threads parse the same Ruby source concurrently, all shall produce identical results
- No thread shall receive another thread's parser state

### Surface Model Types
- Parse results shall use plain C# records/classes (no tree-sitter dependency)
- Each extracted element shall carry source byte range (start byte, end byte) for span creation
- The surface model types shall live in a `Surface/` subdirectory

## Constraints

- **Containment boundary** — all tree-sitter interop is in `RubyTreeSitterClient` and `RubyQueries`. No other class in the project may reference TreeSitter.DotNet types. This enables swapping to Prism later without touching consumers
- **Query strings, not CST traversal** — use tree-sitter's S-expression query language for extraction. The design chose this over visitor pattern for robustness to grammar evolution
- **No materialization** — this plan validates the parser only. The client returns surface model types; conversion to graph nodes is Plan 02's scope

## References

- [Ruby Format Design](../../designs/current/ruby-format.md) — Tree-Sitter Integration section, S-expression queries, thread safety approach
- [Ruby Parsing Research](../../research/ruby-parsing-from-dotnet.md) — TreeSitter.DotNet evaluation, platform coverage, risk assessment
- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — NuGet package with bundled Ruby grammar
- [tree-sitter query syntax](https://tree-sitter.github.io/tree-sitter/using-parsers/queries) — S-expression pattern language
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — project structure conventions
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Parse errors produce partial results, never exceptions. The client catches all tree-sitter exceptions and translates them to diagnostics on the result object.

Native library loading failure at startup is the one hard error — if the grammar can't load, there's nothing to recover. The error message must be actionable: name the NuGet package, the expected RID, and suggest `dotnet restore`.
