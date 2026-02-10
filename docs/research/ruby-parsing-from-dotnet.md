---
description: Research into approaches for parsing Ruby source code from .NET, informing Ruby format support in RepoQL
tags: [ruby, parsing, formats, tree-sitter, prism, antlr]
audience: { human: 50, agent: 50 }
purpose: { research: 90, reference: 10 }
---

# Parsing Ruby from .NET

Research for the decision of how to add Ruby format support to RepoQL — specifically, which parsing approach to use for extracting structure (classes, modules, methods, constants, relationships) from Ruby source files within a .NET/C# indexing pipeline.

*Research date: 2026-02-10*

## Context

RepoQL needs a Ruby format loader following the same pattern as existing loaders (C#/Roslyn, TypeScript/TS Compiler, PHP/ANTLR4). The loader must:

- Extract structural declarations: classes, modules, methods, constants, mixins
- Produce nodes, edges, and spans for the knowledge graph
- Handle malformed files gracefully (errors never cascade)
- Run cross-platform (Windows, Linux, macOS)
- Avoid requiring Ruby installed on the indexing machine (preferred, not mandatory)

**Existing format loader patterns in RepoQL:**

| Format | Parser | Integration | External dependency |
|--------|--------|-------------|-------------------|
| C# | Roslyn (Microsoft.CodeAnalysis) | NuGet, in-process | None |
| TypeScript | TS Compiler API | Node.js subprocess over stdin/stdout JSON | Node.js runtime |
| PHP | ANTLR4 (grammar compiled to C#) | NuGet, in-process | None |

Ruby's syntax presents specific challenges: the `/` operator is ambiguous (division vs regex delimiter), method calls don't require parentheses, and metaprogramming can define symbols dynamically. These make Ruby harder to parse than most languages.

---

## Tree-Sitter via TreeSitter.DotNet

The [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) NuGet package (v1.3.0, January 2026) provides .NET Standard 2.0 bindings to tree-sitter with 28+ bundled language grammars, **including Ruby**. All native binaries for win-x64, win-x86, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 ship inside the single NuGet package.

> [TreeSitter.DotNet on NuGet](https://www.nuget.org/packages/TreeSitter.DotNet) — 14.1K downloads, ~50/day, MIT license

API style:

```csharp
using var language = new Language("Ruby");
using var parser = new Parser(language);
using var tree = parser.Parse(rubySourceCode);

using var query = new Query(language, "(method name: (identifier) @method_name)");
foreach (var capture in query.Execute(tree.RootNode).Captures)
    Console.WriteLine($"Found method: {capture.Node.Text}");
```

The underlying [tree-sitter-ruby](https://github.com/tree-sitter/tree-sitter-ruby) grammar is the canonical Ruby grammar under the official tree-sitter organization. 1,025 commits, 219 stars, 71 forks, v0.23.1 (November 2024). Used by GitHub for code navigation, Neovim, Helix, and Zed.

> [tree-sitter-ruby on GitHub](https://github.com/tree-sitter/tree-sitter-ruby) — official grammar, comprehensive Ruby coverage

The grammar recognizes: `class`, `module`, `method`, `singleton_method`, `singleton_class`, `block`, `do_block`, `lambda`, `call`, `assignment`, `alias`, `begin`/`rescue`/`ensure`, pattern matching (Ruby 3.1), endless methods, and forwarded parameters.

> [tree-sitter-ruby node-types.json](https://github.com/tree-sitter/tree-sitter-ruby/blob/master/src/node-types.json) — full node type catalog

Tree-sitter has built-in error recovery: invalid syntax produces `ERROR` nodes in the tree while the rest parses normally. This aligns with RepoQL's "errors never cascade" constraint.

> [Tree-sitter error recovery](https://www.deusinmachina.net/p/tree-sitter-revolutionizing-parsing) — always returns a usable tree

**Risks:** TreeSitter.DotNet has 10 GitHub stars and a single maintainer (mariusgreuel). Small project, single-point-of-failure risk. Native binaries bundled in one package inflate size. Tree-sitter parsers are not thread-safe — each thread needs its own Parser instance.

Other .NET tree-sitter bindings exist but none are viable for this use case:

| Package | Ruby? | Platforms | Last updated |
|---------|-------|-----------|-------------|
| TreeSitterSharp (Summpot) | No | All | Nov 2023 |
| TreeSitter (profmagija) | No | Linux only | Dec 2019 |
| csharp-tree-sitter (official) | No | Windows only | Sep 2023 |

> [TreeSitterSharp](https://www.nuget.org/packages/TreeSitterSharp), [profmagija/dotnet-tree-sitter](https://github.com/profMagija/dotnet-tree-sitter), [tree-sitter/csharp-tree-sitter](https://github.com/tree-sitter/csharp-tree-sitter) — alternatives without Ruby support

---

## ANTLR4

ANTLR4 has excellent C# target support and is already used by RepoQL for PHP parsing. Three Ruby grammars exist in the ecosystem:

**Corundum** (in [antlr/grammars-v4](https://github.com/antlr/grammars-v4/blob/master/ruby/Corundum.g4)) — described as "a Ruby-like language" grammar targeting Parrot VM IR. The "Ruby-like" qualifier suggests incomplete Ruby coverage.

> [Corundum grammar](https://github.com/antlr/grammars-v4/blob/master/ruby/Corundum.g4) — "Ruby-like", not full Ruby

**multilang-depends/ruby-parser-antlr4** — claims to cover "most of Ruby grammar" targeting Ruby 2.6.0-rc2. 13 commits, January 2019, apparently unmaintained. Known problems: regex-vs-division ambiguity (`a / b + c / d`), extreme parse times on certain files.

> [ruby-parser-antlr4](https://github.com/multilang-depends/ruby-parser-antlr4) — 13 commits, known ambiguity issues

**AlexBelov/ruby-antlr4** — development moved to the Corundum grammar above. 76 commits, dormant.

Ruby's fundamental parsing challenge for ANTLR: the lexer needs parser feedback to disambiguate `/` (division vs regex delimiter) and to handle optional parentheses in method calls. Ruby's grammar is not cleanly context-free, which is why no ANTLR grammar has reached production quality.

> [Ruby parsing ambiguities](https://github.com/multilang-depends/ruby-parser-antlr4) — documented in the parser readme

---

## Prism (Ruby's Official Parser)

[Prism](https://github.com/ruby/prism) is Ruby's official parser, default in Ruby 3.3+, replacing both Ripper and the Parser gem. Written in C99 with zero external dependencies. Used by CRuby, JRuby, TruffleRuby, Sorbet, RuboCop, and Ruby LSP.

> [Prism on GitHub](https://github.com/ruby/prism) — official Ruby parser, C99, zero dependencies
> [Prism in 2024](https://railsatscale.com/2024-04-16-prism-in-2024/) — adoption and integration overview

Two integration paths:

**Process-based:** Shell out to Ruby running a small script that uses `Prism.parse()` and serializes to JSON. Requires Ruby installed on the indexing machine. Similar to how RepoQL's TypeScript loader spawns Node.js.

**Native P/Invoke:** Compile `libprism` as a shared library and call it from C# via P/Invoke. The C API provides `pm_serialize_parse()` which returns a self-contained binary buffer in a documented serialization format (LEB128 varint encoding). A C# deserializer would be needed for the binary format.

> [Prism C API](https://ruby.github.io/prism/c/index.html) — `pm_serialize_parse()` for single-call parsing
> [Prism serialization spec](https://github.com/ruby/prism/blob/main/docs/serialization.md) — documented binary format

The P/Invoke approach eliminates the Ruby runtime dependency but requires compiling and distributing `libprism` native binaries for each platform, plus writing a binary format deserializer.

---

## Regex/Heuristic Parsing

Line-based pattern matching for Ruby declarations. Proven in practice by [universal-ctags](https://github.com/universal-ctags/ctags/blob/master/parsers/ruby.c), which has been used for decades.

> [ctags Ruby parser](https://github.com/universal-ctags/ctags/blob/master/parsers/ruby.c) — line-based, production-proven

**Detectable declarations:**

```ruby
class Foo              # class/module always start lines (possibly indented)
class Foo < Bar        # inheritance
module Bar
def baz(x, y)         # method definitions
def self.baz           # class methods
attr_accessor :name    # accessor declarations
CONSTANT = value       # constant assignment
include Auth           # mixin inclusion
```

**Failure modes:** String interpolation containing keywords (`"class #{Foo}"`), comments (`# class Foo`), heredocs, `eval`/`define_method(:foo)`, `class << self` blocks, multi-line method signatures.

The ctags source acknowledges this: *"this whole scheme is wrong, because Ruby isn't line-based"* — but it works for structural declaration extraction in practice.

A lightweight state machine tracking string/comment/heredoc context mitigates most false positives. Coverage for real-world Ruby files is estimated at ~95% of declarations.

---

## Process-Based (Ripper / Parser gem)

**Ripper** — built into Ruby since 1.9. `Ripper.sexp(code)` produces S-expression arrays, trivially serializable to JSON. Requires Ruby installed.

> [Ripper reference](https://rubyreferences.github.io/rubyref/stdlib/development/ripper.html) — Ruby stdlib

**Parser gem** (whitequark) — production-ready, supports Ruby 1.8 through 3.3, used by RuboCop. Produces well-documented AST nodes with source locations. **Incompatible with Ruby 3.4+** — users should migrate to Prism.

> [Parser gem](https://github.com/whitequark/parser) — mature but superseded by Prism

Both share the same integration pattern: spawn a Ruby process, communicate over stdin/stdout with JSON. Same architecture as RepoQL's TypeScript loader (Node.js subprocess).

---

## Performance

Benchmarks across parsers, plus subprocess overhead data for process-based approaches.

### C-level parse throughput

Measured on 151 `.rb` files from railties 7.2.1.2 (14,625 lines / 455KB). Ruby 3.4.0-preview2 with YJIT, AMD Ryzen 7 3700X.

| Parser | Time (151 files) | Throughput | Relative |
|--------|-----------------|------------|----------|
| Prism (C-level, no Ruby objects) | 9.96ms | 43.6 MB/s | baseline |
| RubyVM::AST (C-level) | 25.52ms | 17.0 MB/s | 2.6x slower |

> [Benchmarking Ruby Parsers (Benoit Daloze)](https://eregon.me/blog/2024/10/27/benchmarking-ruby-parsers.html) — rigorous cross-parser comparison

At Shopify scale: Prism parsed 50,000 Ruby files in 4.49 seconds, peak memory 10.94 MB (~11,136 files/second).

> [Rewriting the Ruby Parser (Rails at Scale)](https://railsatscale.com/2023-06-12-rewriting-the-ruby-parser/) — YARP/Prism origin, large-scale benchmark

### Ruby-level parse + AST walk

Same corpus, including Ruby object allocation overhead:

| Parser | i/s | Time (151 files) | vs Prism |
|--------|-----|-----------------|----------|
| Prism | 27.1 | 36.92ms | baseline |
| RubyVM::AST | 24.3 | 41.20ms | 1.1x slower |
| Ripper.sexp | 11.3 | 88.77ms | 2.4x slower |
| Parser gem | 2.2 | 445.33ms | 12x slower |

> [Benchmarking Ruby Parsers (Benoit Daloze)](https://eregon.me/blog/2024/10/27/benchmarking-ruby-parsers.html)

### Tree-sitter (cross-language extrapolation)

No published tree-sitter-ruby benchmarks found. Extrapolated from other grammars:

| Grammar | File size | Parse time | Source |
|---------|----------|------------|--------|
| Scala (5,835 lines) | ~150KB | 73ms | [eed3si9n](https://eed3si9n.com/fast-scala3-parsing-with-tree-sitter/) |
| Scala (3,971 lines) | ~100KB | 40ms | [eed3si9n](https://eed3si9n.com/fast-scala3-parsing-with-tree-sitter/) |
| Rust (2,157 lines) | ~64KB | 6.48ms | [tree-sitter docs](https://tree-sitter.github.io/tree-sitter/using-parsers/) |
| Python (20,000 lines) | ~500KB | ~120ms | [tree-sitter discussion #3413](https://github.com/tree-sitter/tree-sitter/discussions/3413) |

Typical small-to-medium files (< 1,000 lines): single-digit milliseconds. Performance varies significantly by grammar quality — tree-sitter-haskell was [sped up 52.8x](https://owen.cafe/posts/tree-sitter-haskell-perf/) by eliminating external scanner overhead.

### Subprocess overhead

Ruby VM startup is the dominant cost for process-based approaches:

| Operation | Time | Source |
|-----------|------|--------|
| `ruby -v` (bare startup) | 14ms | [Charles Nutter](https://blog.headius.com/2024/09/jruby-on-crac-part-1-lets-get-cracking.html) |
| `ruby -e 'puts("hello")'` | 62ms | [Charles Nutter](https://blog.headius.com/2024/09/jruby-on-crac-part-1-lets-get-cracking.html) |
| + `require 'prism'` | ~100-200ms (estimated) | No direct measurement found |

**Per-file subprocess** (spawn per file): ~100-200ms per file, dominated by startup. 10,000 files = ~17-33 minutes in startup alone.

**Persistent subprocess** (spawn once, pipe files): one-time ~100-200ms startup, then sub-millisecond IPC per file. Same pattern as RepoQL's TypeScript loader. 10,000 files = ~1-5 seconds total.

### Projected batch performance (10,000 files)

| Approach | Estimated total time | Bottleneck |
|----------|---------------------|------------|
| Prism C library (in-process) | ~1-2s | Parse throughput |
| Tree-sitter C library (in-process) | ~2-5s | Parse throughput (estimated) |
| Prism via persistent subprocess | ~3-5s | IPC serialization |
| Ripper via persistent subprocess | ~8-15s | Slower parse |
| Parser gem via persistent subprocess | ~45-60s | 12x slower than Prism |
| Heuristic (pure C#) | ~1-3s | String processing |
| Per-file Ruby subprocess | ~17-33 min | VM startup per file |

For RepoQL's batch indexing use case, the in-process C library approaches and persistent subprocess approaches are all fast enough that parsing is unlikely to be the bottleneck. The per-file subprocess approach is prohibitively slow.

---

## Comparison

| Dimension | Tree-Sitter (TreeSitter.DotNet) | ANTLR4 | Prism (P/Invoke) | Prism (Process) | Heuristic | Ripper/Parser (Process) |
|-----------|------|--------|------|------|-----------|---------|
| Ruby coverage | ~98% | ~80% (incomplete grammars) | 100% | 100% | ~95% declarations | 100% / 100% |
| 10K files (est.) | ~2-5s | Unknown | ~1-2s | ~3-5s | ~1-3s | ~8-60s |
| Runtime dependency | None (natives bundled) | None (generated C#) | libprism native binaries | Ruby runtime | None | Ruby runtime |
| .NET integration | NuGet package | NuGet + grammar compile | P/Invoke + custom deserializer | Subprocess JSON | Pure C# | Subprocess JSON |
| Error recovery | Built-in (ERROR nodes) | ANTLR recovery rules | Prism error recovery | Prism error recovery | Graceful (skip unparseable) | Varies |
| Cross-platform | All major (bundled) | All (pure .NET) | Must compile per platform | Requires Ruby install | All (pure .NET) | Requires Ruby install |
| RepoQL precedent | None | PHP loader uses ANTLR4 | None | TypeScript loader uses subprocess | None | TypeScript loader uses subprocess |
| Maintenance risk | Single NuGet maintainer | Abandoned grammars | Ruby core team | Ruby core team | Self-maintained | Whitequark (Parser) / Ruby core (Ripper) |
| Implementation effort | Low | Grammar is the problem | Medium-high | Low | Low-medium | Low |

---

## Gaps

- **Tree-sitter-ruby parse speed** — no published benchmarks for the Ruby grammar specifically. The 2-5s estimate for 10K files is extrapolated from Scala/Rust/Python grammars. Grammar quality significantly affects performance (52.8x range observed across grammars).
- **TreeSitter.DotNet real-world usage** — 10 GitHub stars means limited community validation. No evidence of production use at scale. Would need hands-on testing to verify reliability, especially error recovery behavior and memory management.
- **TreeSitter.DotNet P/Invoke overhead** — the .NET binding adds marshaling cost on top of native tree-sitter. No measurements of this overhead found.
- **Prism binary format stability** — the serialization format is documented but its versioning/stability guarantees are unclear. A P/Invoke approach would couple to this format.
- **Prism native compilation for .NET distribution** — no existing NuGet package for `libprism`. Would need to build and package native binaries for all platforms.
- **Heuristic accuracy measurement** — the "~95%" figure is an estimate based on ctags experience, not measured on a representative Ruby corpus.
- **Ruby metaprogramming prevalence** — how much real-world Ruby code relies on `define_method`, `method_missing`, `eval`, etc. for structural definitions? Rails heavy users may have higher metaprogramming density. No data found.
- **TreeSitter.DotNet thread safety in practice** — documentation says parsers aren't thread-safe, but RepoQL's pipeline concurrency model may or may not create contention. Needs testing.

---

## Summary

Five viable approaches exist. They split along two axes: **Ruby coverage** (full parse vs declaration extraction) and **runtime dependency** (self-contained vs requires Ruby/native binaries).

| | Self-contained | Requires external runtime |
|---|---|---|
| **Full parse** | Tree-sitter (NuGet), Prism P/Invoke | Prism process, Ripper/Parser process |
| **Declarations only** | Heuristic, ANTLR4 (if grammar existed) | — |

Speed is not a differentiator among the viable options. All in-process and persistent-subprocess approaches index 10,000 files in under 10 seconds. The only prohibitively slow pattern is spawning a Ruby process per file (~17-33 minutes for 10K files). Prism at the C level is the fastest measured parser (43.6 MB/s), but the difference between approaches is dwarfed by I/O and materialization costs in a real pipeline.

ANTLR4 would be the natural choice given RepoQL's PHP precedent, but no production-quality Ruby grammar exists and the language's ambiguities make one unlikely to emerge.
