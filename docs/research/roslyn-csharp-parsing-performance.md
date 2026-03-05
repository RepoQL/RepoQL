---
description: Research into parsing C# with Roslyn, with particular focus on performance characteristics, memory behavior, and architectural trade-offs for codebase indexing.
tags: [roslyn, csharp, parsing, performance, tree-sitter, syntax-tree, semantic-model]
audience: { human: 70, agent: 30 }
purpose: { research: 90, reference: 10 }
---

# Parsing C# with Roslyn: Performance Research

Research for decisions about C# parsing strategy in RepoQL's indexing pipeline — where thousands of files are parsed concurrently in a long-running host process.

*Research date: 2026-03-03*

## Context

RepoQL indexes codebases into a queryable knowledge graph. The C# format handler (`RepoQL.Formats.DotNet`) already uses Roslyn with a two-tier architecture:

- **Syntax-only tier** (always): `CSharpSyntaxTree.ParseText` + `CSharpInventoryWalker` extracts declarations, namespaces, members, doc comments, attributes, spans.
- **Semantic tier** (optional, when `.csproj` found + `Analysis=true`): `MSBuildWorkspace` provides type resolution, cross-file references, symbol keys, diagnostics.

This research informs: whether the current approach is sound, where the performance cliffs are, what optimizations exist, and whether alternatives (tree-sitter, tiered approaches) would serve better.

---

## Roslyn's Architecture: The Red-Green Tree

Roslyn implements a two-layer immutable syntax tree designed by Eric Lippert. Understanding this is essential for reasoning about performance.

**Green tree** (internal, not exposed via public API):
- Immutable, built bottom-up during parsing
- Nodes store only *width* (character count), not absolute position
- No parent references
- Cached and deduplicated — identical subtrees share green nodes
- 8 bytes of housekeeping per node

**Red tree** (public API — what `SyntaxNode`, `SyntaxToken` etc. expose):
- Immutable facade built lazily top-down around the green tree
- Provides parent references and absolute positions (computed from cumulative widths during descent)
- Red nodes are created only when you traverse to them — visiting 10% of a file's nodes pays red-node cost for only that 10%

**Implications**: The parse step produces green nodes. Walking the tree materializes red nodes on demand. Incremental editing (`WithChangedText`) rebuilds only O(log n) green nodes; the old red tree is discarded (cheap). Identical syntax patterns (e.g., `public`, `void`, common identifiers) can share green nodes across trees.

> [Eric Lippert: Persistence, facades, and Roslyn's red-green trees](https://ericlippert.com/2012/06/08/red-green-trees/) — original design explanation
> [KirillOsenkov: Roslyn Immutable Trees](https://github.com/KirillOsenkov/Bliki/wiki/Roslyn-Immutable-Trees) — detailed internal analysis
> [dotnet/roslyn issue #63172](https://github.com/dotnet/roslyn/issues/63172) — green node access proposal with performance data

---

## Syntax-Only Parsing

### What It Costs

`CSharpSyntaxTree.ParseText` is the entry point. It is:

- **Standalone**: No compilation, no references, no project context, no disk I/O beyond reading source text
- **Thread-safe**: Safe to call concurrently from multiple threads; each call creates an independent parser; resulting trees are immutable
- **Fast**: Milliseconds per file for typical C# source

First parse of a small snippet: ~2,866 object instances, ~2.4 KB heap. Second parse of the same code (green node caching): 16 additional objects, 0.6 KB. The caching operates at the AppDomain level.

> [SpeakRoslyn analysis](https://www.dotnetcodegeeks.com/2015/05/inside-the-net-compiler-platform-performance-considerations-during-syntax-analysis-speakroslyn.html) — object instance measurements

No published benchmark gives a clean "milliseconds per file for ParseText" for files of known size. The Roslyn team maintains benchmarks in [dotnet/performance](https://github.com/dotnet/performance/tree/main/src/benchmarks/real-world/Roslyn) but results are used internally for regression tracking, not published as absolute numbers.

### What It Gives You

Syntax-only analysis provides roughly 80-90% of the structural information needed for a knowledge graph:

| Available from syntax alone | Not available (requires SemanticModel) |
|---|---|
| Class/struct/interface/record/enum declarations | Resolved type of `var` expressions |
| Method/property/field/event signatures | Overload resolution |
| Namespace hierarchy (block and file-scoped) | Cross-file symbol binding |
| Using directives | Type hierarchy resolution (beyond textual base list) |
| Attributes (text form, not resolved) | Nullable flow analysis |
| XML doc comments (structured trivia) | Implicit conversions |
| Modifiers, type parameters, parameter lists | Generic type argument inference |
| Inheritance — base type *names* (not resolved types) | Documentation comment IDs (`M:Namespace.Class.Method`) |
| Full positional information (spans, line numbers) | Extension method binding |
| Method bodies as syntax trees | |
| Error-tolerant output (always produces a tree) | |

RepoQL's `CSharpInventoryWalker` already exploits this — it extracts declarations, members, usings, doc comments, and references from syntax alone.

> [Microsoft Learn: Work with Syntax](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-syntax) — what's available from syntax
> [dotnet/roslyn discussions #63207](https://github.com/dotnet/roslyn/discussions/63207) — alternatives to GetSemanticModel

### CSharpParseOptions That Affect Performance

| Option | Effect | Recommendation |
|--------|--------|----------------|
| `DocumentationMode.None` | Skips structured XML parsing of `///` comments | Use when doc comments are not needed — saves overhead on heavily-documented files |
| `DocumentationMode.Parse` | Parses doc comments into structured trivia nodes | Default; required if extracting doc comments |
| `LanguageVersion` | Controls which syntax is accepted | No meaningful performance difference between versions; use `LanguageVersion.Preview` for maximum coverage |
| `SourceCodeKind` | Regular vs Script | No known performance difference |
| Preprocessor symbols | Controls `#if` branch visibility | No significant performance impact |

> [dotnet/roslyn issue #58210](https://github.com/dotnet/roslyn/issues/58210) — DocumentationMode performance
> [Roslyn Parser.md](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Design/Parser.md) — parser design, language version behavior

### Walking Patterns

| Pattern | Strengths | Best for |
|---------|-----------|----------|
| `CSharpSyntaxWalker` (override Visit* methods) | Depth-first, automatic recursion, tracks nesting context, controls depth via `SyntaxWalkerDepth` | Complex extraction where order and nesting matter |
| `DescendantNodes()` + LINQ | Concise one-liners, no subclassing | Simple targeted queries ("find all method declarations") |
| Manual recursion | Maximum control, can skip subtrees early | Rare — walker provides this via not calling `base.Visit*` |

`SyntaxWalkerDepth` controls cost:
- `Node` — visits only SyntaxNodes (cheapest, most common)
- `Token` — also visits SyntaxTokens
- `Trivia` — also visits whitespace, comments
- `StructuredTrivia` — descends into doc comments, preprocessor directives

RepoQL uses `SyntaxWalkerDepth.Node` in `CSharpInventoryWalker`, which is the correct choice for declaration extraction.

---

## Semantic Analysis: The Performance Cliff

### Cost Model

`Compilation.GetSemanticModel(tree)` itself is cheap — it creates a lightweight wrapper. The expense comes from queries (`GetSymbolInfo`, `GetTypeInfo`, `GetDeclaredSymbol`), which trigger lazy binding.

**Critical change in C# 8+**: Before nullable reference types, querying a `SemanticModel` only bound individual statements. With nullable enabled, the compiler must bind the **entire member body** for flow analysis. Analyzers that called `GetSemanticModel` per-node saw 4-5x slowdowns.

**Recommended pattern** (from Cyrus Najmabadi on the Roslyn team): "Sweep across the Compilation one tree at a time, then release the tree" — allowing GC of per-tree binding caches.

Holding all SemanticModels simultaneously prevents GC of binding caches; memory grows linearly with the number of files.

> [dotnet/roslyn issue #39840](https://github.com/dotnet/roslyn/issues/39840) — SemanticModel caching impact
> [dotnet/roslyn-analyzers issue #3114](https://github.com/dotnet/roslyn-analyzers/issues/3114) — GetSemanticModel anti-pattern

### MSBuildWorkspace Regression (v4.9+)

A major performance regression was introduced when MSBuildWorkspace moved to an external `BuildHost` process:

| Solution | v4.8 | v4.12 | Regression |
|----------|------|-------|------------|
| SharpDevelop (44 projects) | 14.4s | 119.2s | 828% |
| SimplCommerce (34 projects) | 14.0s | 80.4s | 575% |
| CAP (31 projects) | 11.7s | 48.9s | 419% |

**Key mitigation**: Opening the entire solution at once instead of individual projects reduces the penalty from 400-800% to ~20-40%. The Roslyn team acknowledged "there will always be some extra cost to the build host process."

RepoQL currently opens projects individually via `DotNetProjectLocator.FindProject` per file. For repos with many projects, detecting `.sln` files and opening the entire solution once could yield substantial speedups.

> [dotnet/roslyn issue #76679](https://github.com/dotnet/roslyn/issues/76679) — performance drop in project loading
> [dotnet/roslyn issue #23823](https://github.com/dotnet/roslyn/issues/23823) — slow solution load time

### Compilation Memory

- `CSharpCompilation.Create`: ~10 MB
- `CSharpCompilation.Emit`: additional 10-40 MB
- For parse-only scenarios, **no Compilation is needed at all**

> [dotnet/roslyn issue #22219](https://github.com/dotnet/roslyn/issues/22219) — CSharpScript memory measurements

---

## Memory in Long-Running Processes

This is the most critical concern for RepoQL's architecture.

### Known Issues at Scale

| Scenario | Memory | Source |
|----------|--------|--------|
| VS Roslyn service, large MVC project | 13+ GB | [issue #71949](https://github.com/dotnet/roslyn/issues/71949) |
| Background analysis, large solution, ~100 minutes | 45+ GB | [issue #59766](https://github.com/dotnet/roslyn/issues/59766) |
| Syntax tree caching in large solutions | 20-30% of managed heap | [issue #40300](https://github.com/dotnet/roslyn/issues/40300) |
| EditorConfig processing, 352 projects | 165 MB for diagnostic option dictionaries | [issue #38426](https://github.com/dotnet/roslyn/issues/38426) |

### Roslyn's Internal Mitigations

- **RecoverableSyntaxTree**: Files over 4 KB have their syntax trees serialized to disk via `WeakReference`. Under low memory, all trees are written out and reloaded on demand. The 4 KB threshold is configurable.
- **Green node caching**: Identical subtrees share green nodes, reducing allocations.
- **Object pooling**: Roslyn internally pools StringBuilder, Dictionary, HashSet, Stream to minimize GC pressure.
- **Red-green design**: Red nodes for method bodies use weak references — they can be GC'd when not actively traversed.

**Unclear**: Whether `RecoverableSyntaxTree` behavior activates outside Visual Studio when using Roslyn as a NuGet package in a standalone tool. The public `CSharpSyntaxTree.ParseText` API returns a `SyntaxTree`, not a `RecoverableSyntaxTree`.

> [Roslyn Performance Considerations for Large Solutions](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Performance-considerations-for-large-solutions.md)
> [Matt Warren: Roslyn Performance Lessons](https://mattwarren.org/2014/06/05/roslyn-code-base-performance-lessons-part-1/)

### RepoQL's Existing Mitigations

RepoQL already implements several correct patterns:
- `CSharpWorkspaceHost` uses `IMemoryCache` with sliding (60s) and absolute (600s) expiration
- Explicit `ReleaseCompilationResourcesAsync()` nulls out compilation references after analysis
- Concurrency limiter: `Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4))`

### SourceText: Stream vs String

`SourceText.From(Stream)` is slightly faster than `SourceText.From(string)` for larger files (424ms vs 604ms for 25K-line files in one benchmark). For large files, the stream-based approach uses `LargeEncodedText` internally (chunked storage), avoiding Large Object Heap allocations.

> [Roslyn SourceText benchmark gist](https://gist.github.com/m0sa/33506d5610f0a152bc4d)

---

## Benchmark Data

### The Best Data Point: Roslyn.sln Indexing

The single most relevant benchmark for RepoQL's use case:

- **Roslyn solution**: ~450 MB of source code
- **Red-tree indexing** (walking public API nodes): 35 seconds
- **Green-tree indexing** (internal API, proposed in issue #63172): 19 seconds (45% faster, "GBs less allocations")

This represents walking all syntax trees for the purpose of producing indices (like Navigate-To and Find-All-References) — directly analogous to RepoQL's indexing pipeline.

The green-tree API is internal and the Roslyn team's tentative position was "probably not worth exposing." The issue remains open on backlog.

> [dotnet/roslyn issue #63172](https://github.com/dotnet/roslyn/issues/63172) — green node access proposal

### Other Data Points

| Measurement | Value | Context | Source |
|-------------|-------|---------|--------|
| MSBuildWorkspace load of Roslyn.sln | ~4 minutes | Includes MSBuild evaluation, file I/O, project model | [issue #23823](https://github.com/dotnet/roslyn/issues/23823) |
| First JIT warmup | ~2 seconds | Initial Roslyn compilation startup on mid-range i7 | [Rick Strahl blog](https://weblog.west-wind.com/posts/2022/Jun/07/Runtime-CSharp-Code-Compilation-Revisited-for-Roslyn) |
| Subsequent small compilations | 30-50ms | After JIT warmup | Same source |
| Analyzers share of build time | Up to 70% | With many analyzers enabled | [Anthony Simmon blog](https://anthonysimmon.com/optimizing-csharp-code-analysis-for-quicker-dotnet-compilation/) |

### What's Missing

No published benchmark provides:
- Clean "milliseconds per file for ParseText" for files of known size
- "Bytes per line of code" ratio for syntax tree memory
- Direct Roslyn vs tree-sitter comparison on the same C# files, same hardware, same threading
- Memory profile of parse-only (no Compilation) at scale (thousands of files)

---

## Tree-sitter C# as an Alternative

### Grammar Quality

The [tree-sitter-c-sharp](https://github.com/tree-sitter/tree-sitter-c-sharp) grammar claims "comprehensive support for C# 1 through 13.0." Based on the Roslyn grammar, adapted for tree-sitter's GLR parsing. Version 0.23.1 released November 2024.

### Known Gaps (from open issues)

- C# 12 collection expressions (issue #401)
- C# 14 support (issue #392)
- Interpolated verbatim strings (#368) — complete parsing failure
- Nested preprocessor directives (#377) — creates root ERROR nodes
- Interpolated raw string literals (#359) — incorrect parsing
- Ternary with empty collections (#406) — misparsed
- `async`, `var`, `await` cannot be used as identifiers in all valid positions

### Performance vs Roslyn

One comparison: querying 6,000 C# files in under 1 second with tree-sitter (Rust) vs ~30 seconds with Roslyn (C#, single-threaded). The comparison is imperfect — different languages, different threading — but the order-of-magnitude gap is consistent with tree-sitter's design.

Cross-language comparisons (Scala): tree-sitter 10-130x faster than the language's own compiler for pure parsing.

> [John Austin: Fast C# Code-Search in Rust](https://johnaustin.io/articles/2022/blazing-fast-structural-search-for-c-sharp-in-rust)
> [eed3si9n.com: Fast Scala 3 parsing with tree-sitter](https://eed3si9n.com/fast-scala3-parsing-with-tree-sitter/)

### What Tree-sitter Cannot Do

- No type resolution or semantic analysis
- No cross-file reference tracking
- No compilation diagnostics
- No source generator execution
- Single-file only; no project/solution awareness

### Integration with RepoQL

RepoQL already uses `TreeSitter.DotNet` for Go, Rust, Python, Ruby, PHP, and C++. The C# grammar is available through the same package. The integration pattern is well-established.

> [tree-sitter/tree-sitter-c-sharp](https://github.com/tree-sitter/tree-sitter-c-sharp) — grammar repo
> [TreeSitter.DotNet NuGet](https://libraries.io/nuget/TreeSitter.DotNet) — .NET bindings

### ANTLR C# Grammar

The ANTLR C# grammar supports C# 6 and below. **Not maintained for modern C# versions.** Not a viable alternative.

> [antlr/grammars-v4 C# parser](https://github.com/antlr/grammars-v4/blob/master/csharp/CSharpParser.g4)

---

## Tiered Architecture Patterns

GitHub uses a tiered approach at scale:
- **Tree-sitter** parses every file for structural navigation (definitions, references by name)
- **Stack graphs** provide cross-file name resolution without full compilation
- **CodeQL** performs deeper semantic analysis as a separate post-build step
- The system processes 1,000+ pushes per minute across 9 languages including C#

> [Static Analysis at GitHub (ACM Queue, 2021)](https://dl.acm.org/doi/fullHtml/10.1145/3487019.3487022) — architecture overview
> [GitHub semantic: why tree-sitter](https://github.com/github/semantic/blob/main/docs/why-tree-sitter.md) — design rationale

RepoQL already implements a two-tier approach. The question is whether the syntax-only tier should use tree-sitter instead of Roslyn's `CSharpSyntaxTree.ParseText`.

---

## Anti-Patterns

| Anti-Pattern | Why it's bad | What to do instead |
|---|---|---|
| Creating a Compilation just for syntax analysis | 10+ MB unnecessary allocation | Use `CSharpSyntaxTree.ParseText` alone |
| Calling `GetSemanticModel` per-node | Rebinds entire file each time; 4-5x slowdown with nullable | Reuse the SemanticModel from analysis context |
| Holding all SemanticModels simultaneously | Prevents GC of binding caches; memory grows linearly | Process one tree at a time, release the model |
| Querying resolved symbols when syntax suffices | Orders of magnitude more expensive; requires Compilation | Walk SyntaxNode types directly |
| Using LINQ over syntax collections on hot paths | Hidden allocations, delegate creation | Use explicit loops |
| `foreach` on collections without struct enumerators | Boxing allocations per iteration | Use `for` loops |
| Not passing `CancellationToken` to `ParseText` | Cannot abort long parses | Always pass cancellation tokens |
| Ignoring `DocumentationMode.None` | Parses XML trivia unnecessarily | Set `None` when doc comments aren't needed |
| Opening projects individually (MSBuild v4.9+) | 400-800% regression vs opening solution | Detect `.sln` files, open entire solution |

> [dotnet/roslyn issue #25259](https://github.com/dotnet/roslyn/issues/25259) — performance tips and tricks
> [Matt Warren: Performance Lessons Part 1](https://mattwarren.org/2014/06/05/roslyn-code-base-performance-lessons-part-1/)
> [Matt Warren: Performance Lessons Part 2](https://mattwarren.org/2014/06/10/roslyn-code-base-performance-lessons-part-2/)

---

## Comparison

| Dimension | Roslyn ParseText (syntax-only) | Roslyn + MSBuildWorkspace | Tree-sitter C# |
|---|---|---|---|
| C# version coverage | Complete (always current) | Complete | C# 1-13 with gaps |
| Structural extraction | 80-90% of knowledge graph needs | 100% | 80-90% (different gaps) |
| Parse speed | Fast (milliseconds per file) | Minutes for project load | 10-100x faster than compiler parsers |
| Memory per file | ~2.4 KB+ (with caching) | 10+ MB for Compilation | Lower (native, no managed overhead) |
| Thread safety | Fully thread-safe | Immutable but heavy | Thread-safe |
| Error recovery | Excellent (designed for IDE) | Same | Good but opaque |
| Cross-file semantics | No | Yes | No |
| AOT compatible | No | No | Yes (native library) |
| Package size | ~24 MB (CSharp + Common) | ~28 MB + MSBuild SDK | ~5 MB (native + bindings) |
| .NET integration | Pure managed, same process | Same | Native interop via TreeSitter.DotNet |
| Maintenance burden | Ships with .NET SDK | MSBuild SDK dependency | External grammar, must track updates |
| Existing RepoQL code | CSharpInventoryWalker (written) | CSharpWorkspaceHost (written) | Would need new walker |

---

## Additional Considerations

### AOT and Trimming

Roslyn is fundamentally incompatible with NativeAOT — it relies on reflection, dynamic assembly generation, and runtime code generation. If RepoQL ever ships as a NativeAOT binary, Roslyn-based parsing would need a separate non-AOT process or replacement with tree-sitter. The current two-process architecture (gRPC host) already enables this separation.

> [Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

### C# Version Compatibility

| Roslyn NuGet Version | C# Language Version |
|---|---|
| 4.8.x | C# 11 |
| 4.9.x - 4.11.x | C# 12 |
| 4.12.x - 4.14.x | C# 13 |
| 5.0.x | C# 14 |

Pinning an older Roslyn version will fail to parse files using newer C# features. `LanguageVersion.Preview` in `CSharpParseOptions` provides maximum coverage for the installed Roslyn version.

> [Roslyn Language Feature Status](https://github.com/dotnet/roslyn/blob/main/docs/Language%20Feature%20Status.md)
> [Roslyn version mappings](https://learn.microsoft.com/en-us/visualstudio/extensibility/roslyn-version-support?view=vs-2022)

### Syntax Tree Serialization

Persisting parsed syntax trees to disk (to avoid re-parsing unchanged files) is not viable:
- Internal serializer has no versioning story — trees from one Roslyn version cannot be deserialized by another
- Binary format has internal table dependencies that make concurrent serialization difficult
- Not designed for persistence across process lifetimes

The correct approach (which RepoQL already uses) is to cache the *extracted data* (nodes, edges, spans) in DuckDB rather than the syntax trees themselves. Use file content hashes to skip unchanged files.

> [dotnet/roslyn issue #28621](https://github.com/dotnet/roslyn/issues/28621) — serialization CPU usage

### Existing Codebase Graph Tools Using Roslyn

- **[Strazh](https://github.com/vladbatushkov/strazh)**: .NET ETL that extracts codebase structure into a knowledge graph using Roslyn semantic model
- **[Axon](https://github.com/harshkedia177/axon)**: Indexes codebases into structural knowledge graphs with call chains, exposes via MCP

Neither appears to address the scale/performance concerns that RepoQL faces.

---

## Gaps

- **No clean per-file parse time benchmark exists** — the Roslyn team does not publish absolute numbers, and the dotnet/performance benchmarks are used internally only
- **No bytes-per-line ratio for syntax tree memory** — infrastructure overhead makes isolation difficult without running a profiler
- **No direct Roslyn vs tree-sitter C# benchmark on same files, same hardware** — the existing comparisons conflate language runtime and threading differences
- **RecoverableSyntaxTree behavior outside Visual Studio is unclear** — unknown whether serialization-to-disk activates in standalone tools
- **GC gen2 behavior with thousands of long-lived SyntaxTree objects** — not addressed in any source found
- **Green tree access** is internal-only — issue #63172 showed 45% improvement for indexing Roslyn.sln, but no public API exists
- **MSBuildWorkspace v4.9+ regression**: per-project cost jumped 400-800%; solution-level loading mitigates to ~20-40% but requires architectural awareness
- **Memory profile of parse-only at scale** — most measurements conflate parsing with compilation, metadata loading, and analyzer execution
- **Stack graphs for C# cross-file resolution** — GitHub uses this approach but no .NET library exists; could be a future avenue

---

## Summary

Roslyn's syntax-only parsing (`CSharpSyntaxTree.ParseText` + `CSharpSyntaxWalker`) is performant, thread-safe, and provides 80-90% of the structural information needed for a knowledge graph. The current RepoQL architecture is sound.

The semantic tier (MSBuildWorkspace) is where performance cliffs live: memory grows to GB scale in long-running processes, the v4.9+ regression introduced 400-800% slowdowns for per-project loading, and SemanticModel queries are expensive (especially with C# 8+ nullable analysis). RepoQL's existing mitigations (caching, concurrency limits, explicit resource release) are the right patterns.

Tree-sitter is 10-100x faster for pure parsing but has gaps in modern C# coverage (collection expressions, interpolated strings, preprocessor edge cases). For RepoQL, which already has a working Roslyn-based syntax walker, switching to tree-sitter for the syntax tier would trade completeness for speed — a trade-off whose value depends on whether syntax-only parsing is actually a bottleneck (the evidence suggests the semantic tier is where time is spent).

The most impactful optimization opportunities are in the semantic tier: opening solutions instead of individual projects, process-level isolation for MSBuildWorkspace, and monitoring for memory creep.
