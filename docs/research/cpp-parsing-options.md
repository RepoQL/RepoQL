# C/C++ Parsing Options for RepoQL

Research for selecting a C/C++ parsing approach for RepoQL's format system.

*Research date: February 23, 2026*

## Context

RepoQL needs to parse C and C++ source files into its knowledge graph (artifact, node, edge, span, annotation). The parser must extract structural information — functions, classes, structs, enums, namespaces, includes, templates, macros — and produce the same record types that existing format loaders emit.

**Existing precedents in the codebase:**
- **C#** uses Roslyn — in-process, full semantic analysis, MSBuild workspace integration
- **Ruby** uses `TreeSitter.DotNet` NuGet package — in-process, syntax-only, zero external dependencies
- **TypeScript** shells out to Node.js — external process, JSON over stdio

**Constraints from CLAUDE.md and architecture:**
- Must run on a developer laptop (no cloud, no GPU, no containers)
- Must not block the pipeline — one bad file never breaks anything else
- Must produce records fitting the frozen 5-table schema
- Format code must be hardened against hangs, crashes, unexpected failures
- Time-to-usable is a primary KPI — setup friction is existential

**C/C++ specific challenges:**
- Preprocessor (`#define`, `#include`, `#ifdef`) fundamentally alters source before compilation
- Template metaprogramming creates constructs that are syntactically valid but semantically complex
- No standard build system — CMake, Make, Meson, Bazel, MSBuild, etc.
- Header dependencies mean a single `.cpp` file may transitively include thousands of headers
- Macro-heavy codebases (Linux kernel, Qt, GTK+, Windows SDK) define functions and classes via macros

---

## Tree-Sitter

Incremental parsing library producing concrete syntax trees. Zero dependencies. Generated parsers are pure C.

### Capabilities

Tree-sitter-c (v0.24.1, May 2025) and tree-sitter-cpp (v0.23.4, Nov 2024) are official grammars maintained under the tree-sitter GitHub organization. tree-sitter-cpp extends tree-sitter-c, inheriting all C parsing rules.

**Extractable symbols:**
- Functions (`function_definition`, `function_declarator`, `parameter_list`)
- Classes, structs, unions (`class_specifier`, `struct_specifier`, `union_specifier`)
- Enums (`enum_specifier`, `enumerator_list`)
- Namespaces (`namespace_definition`, `using_declaration`)
- Templates (`template_declaration`, `template_parameter_list`)
- Preprocessor directives (`preproc_include`, `preproc_def`, `preproc_ifdef`)
- Access specifiers (`access_specifier` — public/private/protected)
- Lambdas, static asserts, extern "C" blocks, concept definitions (C++20)

**C++ standard coverage:** Broadly C++11/14/17. C++20 concepts present. C++20 module syntax (`module_declaration`, `import_declaration`, `export_declaration`, `module_name`, `module_partition`, `global_module_fragment_declaration`, `private_module_fragment_declaration`) was merged to master on February 6, 2025 via [PR #266](https://github.com/tree-sitter/tree-sitter-cpp/pull/266), resolving [Issue #174](https://github.com/tree-sitter/tree-sitter-cpp/issues/174). **Not yet in a released version** — the latest release is v0.23.4 (November 2024), predating the merge. The grammar also now includes C++26 experimental features on master: reflection (P2996), annotations (P3394), and expansion statements (P1306).

> [tree-sitter-cpp repository](https://github.com/tree-sitter/tree-sitter-cpp) — grammar and node types
> [tree-sitter-cpp DeepWiki](https://deepwiki.com/tree-sitter/tree-sitter-cpp) — standard coverage analysis

### .NET Integration

**`TreeSitter.DotNet`** (NuGet, v1.3.0) — already used by `RepoQL.Formats.Ruby`. Bundles native tree-sitter library + language grammars. Platforms: Windows (x86, x64, arm64), Linux (x86, x64, arm, arm64), macOS (x64, arm64). Supports predicate queries.

**Critical finding:** `TreeSitter.DotNet` is based on [profMagija/dotnet-tree-sitter](https://github.com/profMagija/dotnet-tree-sitter), whose last commit was March 2022. The repository's `.gitmodules` pins only three language grammars: C, JavaScript, and Python. **C++ is not bundled.** The pinned tree-sitter-c submodule points to commit `e348e8ec` (tree-sitter-c v0.20.1, September 2021) — approximately 4 years behind the current v0.24.1 (May 2025). Adding C++ support would require building and bundling the tree-sitter-cpp grammar separately, or contributing it upstream.

The Ruby format's `RubyTreeSitterClient` demonstrates the integration pattern: `new Language("tree-sitter-ruby", "tree_sitter_ruby")` creates the parser, `parser.Parse(sourceCode)` produces a tree, S-expression queries extract structured data. Thread-safe via `ThreadLocal<Parser>`.

> [TreeSitter.DotNet on Libraries.io](https://libraries.io/nuget/TreeSitter.DotNet) — package info
> [profMagija/dotnet-tree-sitter](https://github.com/profMagija/dotnet-tree-sitter) — source repository (last updated March 2022)
> `src/Formats/RepoQL.Formats.Ruby/TreeSitter/RubyTreeSitterClient.cs` — existing integration pattern

Other .NET bindings exist (`TreeSitter.Bindings` v0.4.0, official [`csharp-tree-sitter`](https://github.com/tree-sitter/csharp-tree-sitter), `dotnet-tree-sitter`). The official `csharp-tree-sitter` references a fork of tree-sitter-cpp (`DennySun2100/tree-sitter-cpp`), not the canonical grammar. None of these are mature or proven in this codebase.

### Limitations

**Preprocessor:** Tree-sitter parses source text as-is without running the preprocessor. `#include` directives are parsed as nodes but headers are not read. `#define` macros are parsed but not expanded. Macros that expand to syntactic fragments (type specifiers, function bodies) produce `ERROR` nodes. `#ifdef` blocks that split syntactic constructs cause parse errors. This is an architectural limitation, not a fixable bug.

> [tree-sitter-c Issue #108](https://github.com/tree-sitter/tree-sitter-c/issues/108) — preprocessor macros not general enough
> [tree-sitter-cpp Issue #136](https://github.com/tree-sitter/tree-sitter-cpp/issues/136) — macros break parsing

**No semantic analysis:** Cannot resolve names, check types, or connect two identifiers referring to the same symbol. GitHub previously used [stack-graphs](https://github.com/github/stack-graphs) on top of tree-sitter for precise code navigation, but decommissioned that system in March 2025 and archived the repository in September 2025.

**Template metaprogramming:** Template syntax is recognized structurally but no instantiation, type checking, or SFINAE evaluation.

**Error recovery:** Purpose-built for editors — invalid constructs produce `ERROR` nodes without affecting parsing of subsequent text. Quality varies by grammar design.

> [Jake Zimmerman: Is tree-sitter good enough?](https://blog.jez.io/tree-sitter-limitations/) — limitations analysis

### Known Macro Impact on Real Codebases

No formal benchmark exists measuring tree-sitter C++ parse error rates across real codebases. The evidence is qualitative, from issue reports against specific frameworks:

**Qt (`Q_OBJECT`, `Q_CLASSINFO`, `Q_DISABLE_COPY`):** Macros at the beginning of a class body cause the rest of the class to mis-parse. The class's methods and members after the macro are incorrectly attached or produce ERROR nodes. Reported as "a garbage syntax tree with bitfield nodes that aren't really there."

> [tree-sitter-cpp#136](https://github.com/tree-sitter/tree-sitter-cpp/issues/136), [tree-sitter-cpp#85](https://github.com/tree-sitter/tree-sitter-cpp/issues/85)

**Windows DLL export macros (`EXPORT_API`, `__declspec(dllexport)`):** `class EXPORT_API MyClass { ... }` is misinterpreted as a function declaration. This is a common pattern in Windows C++ codebases.

> [tree-sitter-cpp#85](https://github.com/tree-sitter/tree-sitter-cpp/issues/85)

**Google Test (`ASSERT_DEBUG_DEATH`, `TEST_CASE_TEMPLATE`):** Test macros produce ERROR nodes. doctest's `TEST_CASE_TEMPLATE` produces an ERROR node that consumes the entire test body.

> [tree-sitter-cpp#328](https://github.com/tree-sitter/tree-sitter-cpp/issues/328)

**GCC `__attribute__` on struct fields:** `int bar __attribute__((__aligned__(4)));` inside a struct generates `MISSING ;` and `MISSING type_identifier` nodes.

> [tree-sitter-c#74](https://github.com/tree-sitter/tree-sitter-c/issues/74)

**`#ifdef __cplusplus extern "C"` (common C header pattern):** The `#ifdef __cplusplus extern "C" { #endif ... } #endif` pattern produces `MISSING #endif` nodes because tree-sitter cannot balance preprocessor directives across the linkage specification boundary.

> [tree-sitter-c#108](https://github.com/tree-sitter/tree-sitter-c/issues/108)

**Linux kernel headers:** Large macro-heavy headers (e.g., `bif_5_1_sh_mask.h`, 2 MB) cause severe performance issues through recursive injection queries, requiring workarounds in editors.

> [nvim-treesitter#1292](https://github.com/nvim-treesitter/nvim-treesitter/issues/1292), [nvim-treesitter#5603](https://github.com/nvim-treesitter/nvim-treesitter/issues/5603)

**Namespace macros (Pixar USD `PXR_NAMESPACE_USING_DIRECTIVE`, etc.):** Namespace macros break parsing of everything that follows them in the file.

> [tree-sitter-cpp#85](https://github.com/tree-sitter/tree-sitter-cpp/issues/85)

The tree-sitter-cpp maintainer states definitively: *"Macros cannot be 100% correctly parsed by a grammar that isn't contextually aware and doesn't run the preprocessor, unfortunately. These ambiguities will always rely on tree-sitter's error recovery."* The tree-sitter author closed the corresponding tree-sitter-c issue as won't-fix: *"I'm gonna close this one out because I think we're doing the best we can under our constraints."*

> [tree-sitter-cpp#136](https://github.com/tree-sitter/tree-sitter-cpp/issues/136) — @jdrouhard (maintainer)
> [tree-sitter-c#7](https://github.com/tree-sitter/tree-sitter-c/issues/7) — @maxbrunsfeld (tree-sitter author)

### Who Uses It for C/C++

| Project | Usage |
|---------|-------|
| GitHub | Search-based code navigation (tree-sitter tag queries), code search symbol extraction, syntax highlighting via Linguist |
| Neovim | Built-in syntax highlighting, code folding, text objects since v0.5 |
| Zed | Core parsing engine (created by tree-sitter's author) |
| Helix | Syntax highlighting and structural navigation |
| Emacs 29+ | Native tree-sitter modes (`c-ts-mode`, `c++-ts-mode`) |
| Aider | Repository maps for LLM context via `grep-ast` |
| ast-grep | Structural code search/lint built on tree-sitter |

**GitHub's C++ code navigation history:** In late 2021, GitHub [disabled C++ from the code search tech preview](https://github.com/orgs/community/discussions/8594) because C++ was "the most resource intensive parser with a very high failure and timeout rate." C++ later gained **search-based** code navigation (symbol extraction via tree-sitter tag queries, matched by name across the repository). The more ambitious **precise** code navigation (powered by [stack-graphs](https://github.com/github/stack-graphs)) — which C++ never had — was [decommissioned entirely in March 2025](https://github.com/github/code-navigation/pull/15) and the stack-graphs repository was [archived in September 2025](https://github.com/github/stack-graphs). As of early 2026, all GitHub code navigation for all languages is search-based, and C++ is among the 20 supported languages.

> [GitHub code-navigation](https://github.com/github/code-navigation) — architecture and supported languages
> [code-navigation PR #15](https://github.com/github/code-navigation/pull/15) — precise code navigation decommissioned
> [Aider: Building a better repository map](https://aider.chat/2023/10/22/repomap.html) — tree-sitter for LLM context

### License

Tree-sitter core, tree-sitter-c, and tree-sitter-cpp are all MIT licensed.

---

## libclang / Clang

Stable C API to the Clang compiler frontend (LLVM project). Full semantic analysis at the translation unit level.

### Capabilities

libclang performs the same front-end processing as the Clang compiler: preprocessing, parsing, and semantic analysis. The AST contains resolved types, instantiated templates, and expanded macros.

**What you get beyond tree-sitter:**
- Full type resolution (`CXType` with qualifiers, pointers, canonical types)
- Template instantiation and specialization tracking
- Macro expansion (macros are resolved in the AST)
- Unified Symbol Resolutions (USRs) for cross-translation-unit symbol matching
- Compiler diagnostics with source locations, severity, and fix-it hints
- Documentation comment extraction
- Declaration/definition/reference navigation within a translation unit

**"Unexposed" cursor kinds:** Some AST nodes report as `CXCursor_UnexposedStmt` or `CXCursor_UnexposedExpr` — the node exists and can be traversed but the specific kind is not reported. By design: libclang prioritizes IDE-like use cases. This has improved over time.

> [Libclang tutorial — Clang documentation](https://rocm.docs.amd.com/projects/llvm-project/en/latest/LLVM/clang/html/LibClang.html) — API overview
> [Using libclang to Parse C++](https://shaharmike.com/cpp/libclang/) — practical guide

### .NET Integration

**ClangSharp** (`dotnet/ClangSharp`, MIT license, 1.2k stars) — under the `dotnet` GitHub org. Strongly-typed P/Invoke bindings. Tracks LLVM releases (currently at 21.1.8). Maintained by Tanner Gooding (Microsoft .NET team). Native binary: ~37 MB for `libclang.runtime.win-x64` + ~2.5 MB for `libClangSharp.runtime.win-x64`. Each platform needs its own binary.

**CppAst.NET** (`xoofx/CppAst.NET`, BSD-2-Clause, 594 stars) — higher-level library on top of libclang. Produces a simplified AST model (`CppCompilation` with `CppClass`, `CppFunction`, `CppField`). Uses Clang 20.1.2.4. Managed package: 93 KB (plus ClangSharp native binaries). Primary use case: PInvoke/interop generation, but usable as a general header parser.

**CppSharp** (`mono/CppSharp`, MIT, 3.3k stars) — binding generator with rich parser. Clang 19. Package size: ~98 MB (includes native binaries). 67+ contributors. Pre-compiled binaries Windows-only; other platforms require building from source.

> [ClangSharp GitHub](https://github.com/dotnet/ClangSharp) — .NET Foundation project
> [CppAst.NET GitHub](https://github.com/xoofx/CppAst.NET) — simplified AST model
> [CppSharp GitHub](https://github.com/mono/CppSharp) — binding generator

### Requirements

**Compilation database:** Not strictly required — files can be parsed with explicit flags (`-std=c++17`, `-I/path`). Strongly recommended for accurate results. Common formats: `compile_commands.json` (CMake, Bear) or `compile_flags.txt`.

**Headers:** libclang needs headers to produce a complete AST. Missing `#include` targets produce unresolved references in the AST. Clang's own built-in headers must match the libclang version exactly — mismatched versions cause cascading errors.

**What happens without proper configuration:** By default, a missing `#include` generates a **fatal error** that stops parsing — you get essentially nothing after the first unresolvable include. The `CXTranslationUnit_KeepGoing` flag (Clang 3.9+) changes this: *"Do not stop processing when fatal errors are encountered... For the purposes of an IDE, this is undesirable behavior and as much information as possible should be reported."*

With `KeepGoing`, Clang produces a partial AST. Clang maintainer @AaronBallman: *"We try to form as complete of an AST as we can, but there are several circumstances under which we don't retain anything. For example, to avoid cascading errors, we often will try to break out of the lexical context."* Concrete degradation: every type from a missing header becomes unresolved, triggering cascading `undeclared identifier` errors; functions using those types lose type information; template instantiations with unresolved types are dropped; entire scopes may be skipped during error recovery. `RecoveryExpr` nodes preserve subexpressions of failed constructs but have "no semantic meaning in C++."

This is a specific risk for RepoQL's "results trustworthy or loudly not" promise. The partial AST looks structurally valid but may be silently missing symbols.

> [Clang Index.h — CXTranslationUnit flags](https://oberon00.github.io/synth/lib-llvm/include/clang-c/Index.h.html) — KeepGoing flag documentation
> [clangd system-headers guide](https://github.com/llvm/clangd-www/blob/main/guides/system-headers.md) — header resolution
> [Clang Emit AST with Errors](https://github.com/llvm/llvm-project/issues/59700) — @AaronBallman on incomplete parse behavior

### Performance

| Scenario | Time | Source |
|----------|------|--------|
| Qt Creator TU registration (MSVC headers, Windows) | ~9,500 ms | [Speeding up libclang on Windows](https://cristianadam.eu/20160104/speeding-up-libclang-on-windows/) |
| Same with optimized libclang build | ~6,000 ms | Same source |
| Reparse with precompiled preamble | ~3 ms | Multiple sources |
| Header-only template library (NVIDIA CUTLASS) | 10+ seconds | [clangd Issue #2186](https://github.com/clangd/clangd/discussions/2186) |

Memory: ~30 MB per open translation unit. Scales with header complexity, not source file size. Native library: ~37 MB on Windows.

`CXTranslationUnit_SkipFunctionBodies` flag skips function/method bodies — significant performance improvement when only declarations are needed.

> [Speeding up libclang on Windows (2019)](https://cristianadam.eu/20190318/speeding-up-libclang-on-windows/) — optimization strategies

### Limitations

**Heavyweight dependency:** ~37 MB native library per platform. Each platform (win-x64, linux-x64, osx-arm64) needs its own binary. Building LLVM from source requires 15-70+ GB disk space (irrelevant for consumers using NuGet, but indicates ecosystem weight).

**Silent incomplete results:** See "What happens without proper configuration" above. Without `KeepGoing`, parsing stops at the first missing include. With `KeepGoing`, the AST is structurally valid but silently incomplete. Both modes violate RepoQL's "results trustworthy or loudly not" promise without explicit handling.

**Single-file orientation:** Each `clang_parseTranslationUnit` call processes one translation unit. Cross-TU analysis requires managing multiple TUs and matching USRs.

**Setup friction:** Requires headers, may require compilation database, needs platform-specific native binaries. Significantly higher setup burden than tree-sitter.

**No incremental parsing:** No keystroke-level incremental reparsing (precompiled preambles help with reparse but initial parse is slow).

### License

Apache 2.0 with LLVM Exceptions. Bindings: ClangSharp (MIT), CppAst.NET (BSD-2-Clause), CppSharp (MIT).

---

## clangd (LSP)

LSP server built on Clang. Full compiler-grade analysis via JSON-RPC protocol.

### Capabilities

Everything Clang understands: functions, classes, structs, enums, namespaces, templates, macros, variables, typedefs, concepts, modules. Document symbols, semantic tokens, references, call hierarchy, diagnostics with clang-tidy.

Can be spawned as a child process and communicated with programmatically. C# LSP client library available at [OmniSharp/csharp-language-server-protocol](https://github.com/OmniSharp/csharp-language-server-protocol).

> [clangd Features](https://sam-mccall.github.io/clangd-www/features.html) — capability list

### Requirements

Requires `compile_commands.json`. Without it, limited fallback functionality. System and project headers must be resolvable. Binary dependency: ~39 MB stripped (clangd v18.1.7) to ~75 MB stripped (v19.1.7) on Windows; unstripped builds can reach 268 MB.

> [clangd#2341](https://github.com/clangd/clangd/issues/2341) — binary size v18 vs v19
> [clangd#2316](https://github.com/clangd/clangd/issues/2316) — stripped vs unstripped

### Performance

Background indexing stores on-disk index in `.cache/clangd/index/`. Initial indexing of large projects (LLVM, Chromium) takes minutes to hours (130 seconds measured for the Ladybird browser project). Memory: multiple GB for large projects — one instance on LLVM with 19 open files consumed ~20 GB after ~4 days. Per-file preamble build: ~10-14 seconds for files with complex includes (e.g., 99 MB preamble), ~1 second for simple files.

> [clangd Indexing Design](https://sam-mccall.github.io/clangd-www/design/indexing.html) — architecture
> [clangd Issue #1690](https://github.com/clangd/clangd/issues/1690) — 14s preamble build per file
> [clangd Issue #1165](https://github.com/clangd/clangd/issues/1165) — 10s+ per file on reopen
> [clangd Issue #251](https://github.com/clangd/clangd/issues/251) — memory consumption
> [clangd Issue #2355](https://github.com/clangd/clangd/issues/2355) — performance on large projects
> [zed-industries/zed#14124](https://github.com/zed-industries/zed/issues/14124) — 130s initial indexing

### Assessment for RepoQL

clangd is designed for interactive editor use — long-running backend for one workspace. Using it as a batch parsing backend introduces operational complexity: process lifecycle management, LSP protocol overhead for simple symbol extraction, extreme memory/time requirements for large projects. The subprocess pattern exists in RepoQL (TypeScript uses Node.js), but clangd's resource profile is categorically different.

### License

Apache 2.0 with LLVM Exceptions.

---

## Universal Ctags

Heuristic tag extractor. Single binary, no compilation database, no header resolution.

### Capabilities

Rewrote the C/C++ parser from scratch in 2016. Extracts: classes, macros, enumerators, functions, enum names, members, namespaces, prototypes, structs, typedefs, unions, variables, namespace aliases, goto labels, using declarations, template parameters.

Output formats: traditional tags file (tab-separated, trivial to parse) or JSON (`--output-format=json`).

**Speed:** 27,886 C files (19.8M lines, 555 MB) in 15.1 seconds (~36,686 KB/s). Single-threaded. With macro expansion enabled, speed roughly halves.

> [Universal Ctags C++ parser docs](https://docs.ctags.io/en/latest/parser-cxx.html) — parser details
> [Universal Ctags GitHub](https://github.com/universal-ctags/ctags) — repository

### Limitations

**Templates:** Known issues with template template parameters and complex metaprogramming. `std::hash<Key>` constructs produce incorrect results.

> [Issue #2060](https://github.com/universal-ctags/ctags/issues/2060) — template parsing failures

**Preprocessor:** Heuristic `-D` flag for macros, not a full preprocessor. Macro expansion makes parsing ~2x slower.

**No cross-file analysis:** Each file parsed independently. No include resolution.

**License:** GPL-2.0-or-later. This copyleft license affects distribution of derivative works and may be incompatible with RepoQL's licensing.

---

## srcML

Converts C/C++ source to XML with AST annotations. Lossless round-tripping.

### Capabilities

Marks up: functions, classes, structs, unions, enums, namespaces, statements, declarations, expressions, preprocessor directives (in `cpp:` namespace). Preprocessor directives are marked up but NOT expanded.

Speed: ~35 KLOC/sec. Linux kernel convertible in ~7 min. XPath/XSLT transformations at 80+ KLOC/sec. Version 1.1.0 released August 2025.

> [srcML About](https://www.srcml.org/about.html) — project overview
> [srcML ICSM13 Paper](https://www.cs.kent.edu/~jmaletic/papers/ICSM13-srcML.pdf) — benchmarks

### .NET Integration

Command-line tool (`srcml`) or `libsrcml` C library (P/Invoke possible). No existing .NET bindings. XML output consumable with `System.Xml.Linq`. Binary size: under 1 MB on Linux (Fedora 38 RPM: 791 KB); Windows installers may be larger due to bundled dependencies (libxml2, libxslt).

> [srcML releases](https://github.com/srcML/srcML/releases) — v1.1.0 package sizes

### Assessment for RepoQL

XML intermediary adds conversion overhead. No existing .NET bindings requires building them or parsing XML. srcML's strength (lossless round-tripping) isn't needed for RepoQL's use case (structural extraction, not transformation). Preprocessor not expanded — same fundamental limitation as tree-sitter.

### License

GPL. Incompatible with permissive licensing.

---

## ANTLR4

Parser generator with C and C++ grammars in the `grammars-v4` repository.

### Capabilities

C grammar targets C11. C++ grammar targets C++14 — no C++17 or C++20 grammar exists. The C++ grammar is "basically copied from the C++ standard appendix which is far from an efficient form."

**Real-world validation:** A practical study achieved only 71% success rate (102 of 143 files parsed without error).

ANTLR4 C# target is mature (`Antlr4.Runtime.Standard` v4.13.1 on NuGet, .NET Standard 2.0). The C++ grammar contains Java semantic predicates that must be rewritten for the C# target.

**Preprocessor:** Ignored entirely. Input must be externally preprocessed.

> [ANTLR4 grammars-v4](https://github.com/antlr/grammars-v4) — grammar repository
> [C++ grammar practicality validation](https://twoflat.medium.com/validated-the-practicality-of-antlr4s-c-parser-79605b4733ed) — 71% success rate
> [Issue #2475](https://github.com/antlr/grammars-v4/issues/2475) — no C++17/20 grammar

### Assessment for RepoQL

71% parse success rate violates "results trustworthy or loudly not." C++14-only coverage is insufficient for modern codebases. Preprocessor must be handled externally. Grammar quality is below what tree-sitter achieves for the same task.

### License

BSD (runtime and grammars). Compatible.

---

## Comparison

### Core Capabilities

| Dimension | Tree-Sitter | libclang | clangd | Universal Ctags | srcML | ANTLR4 |
|-----------|------------|----------|--------|-----------------|-------|--------|
| Parse level | Syntax (CST) | Full semantic | Full semantic | Heuristic tags | Syntax (XML) | Syntax (AST) |
| Preprocessor | Directives as nodes, no expansion | Full expansion | Full expansion | Heuristic, `-D` flag | Marked up, no expansion | Ignored entirely |
| Header resolution | No | Yes (required) | Yes (required) | No | No | No (must preprocess) |
| Template handling | Syntactic only | Full instantiation | Full instantiation | Heuristic | Syntactic only | Syntactic (C++14 only) |
| Error recovery | Purpose-built, robust | Compiler-grade diagnostics | Compiler-grade diagnostics | Heuristic | Varies | 71% real-world success |

### Operational Characteristics

| Dimension | Tree-Sitter | libclang | clangd | Universal Ctags | srcML | ANTLR4 |
|-----------|------------|----------|--------|-----------------|-------|--------|
| Native binary size | Hundreds of KB per grammar ^1 | ~37 MB per platform | ~39-75 MB stripped ^2 | Small (single binary) | <1 MB on Linux ^3 | None (generates source) |
| Parse speed (per file) | Sub-100ms for typical files ^4 | 100ms-10s+ (header dependent) | 10-14s preamble per file ^5 | Very fast (~36 MB/s) | ~35 KLOC/s ^6 | Varies |
| Memory per file | Minimal | ~30 MB per TU ^7 | Multi-GB for workspace | Minimal | Moderate | Moderate |
| External dependencies | None | Headers + optional compile_commands.json | compile_commands.json + headers | None | None | None |
| Setup friction | Zero | Moderate-high | High | Low (subprocess) | Low (subprocess) | Low |
| Incremental parsing | Native, sub-millisecond | Precompiled preamble only | Background indexing | None | None | None |

*^1 Generated parser.c for tree-sitter-c is ~3.9 MB source; compiled size varies by platform. [tree-sitter-c repo](https://github.com/tree-sitter/tree-sitter-c). ^2 clangd v18: 39 MB, v19: 75 MB stripped on Windows. [clangd#2341](https://github.com/clangd/clangd/issues/2341). ^3 Fedora 38 RPM: 791 KB. [srcML releases](https://github.com/srcML/srcML/releases). ^4 Scala 5,835 lines: 60ms; LLVM ir.cpp: 168ms; very large files exceed 100ms. [tree-sitter#1467](https://github.com/tree-sitter/tree-sitter/discussions/1467), [eed3si9n.com](https://eed3si9n.com/fast-scala3-parsing-with-tree-sitter/). ^5 Per-file preamble build, not initial indexing which takes minutes. [clangd#1690](https://github.com/clangd/clangd/issues/1690). ^6 [srcML ICSME16 paper](https://www.cs.kent.edu/~jmaletic/papers/ICSME16-srcML.pdf). ^7 STL includes; Boost can push to 150-500 MB per TU. [clang_complete#142](https://github.com/xavierd/clang_complete/issues/142).*

### RepoQL Fit

| Dimension | Tree-Sitter | libclang | clangd | Universal Ctags | srcML | ANTLR4 |
|-----------|------------|----------|--------|-----------------|-------|--------|
| Existing codebase precedent | Partial — TreeSitter.DotNet used for Ruby but **does not bundle C++ grammar**; integration pattern proven, grammar must be added | No | No (subprocess pattern exists for TS) | No | No | No (ANTLR infrastructure exists in RepoQL.Grammar) |
| Implementation model | Follow Ruby loader pattern: TreeSitterClient + S-expression queries → DocumentModel | Follow TypeScript subprocess pattern, or in-process via ClangSharp P/Invoke | Subprocess over JSON-RPC (like TS loader but heavier protocol) | Subprocess, parse tags output | Subprocess, parse XML output | In-process, generated parser |
| Laptop-friendly | Yes | Marginal (37 MB binary, header deps) | No (GB-scale memory) | Yes | Yes | Yes |
| Time-to-usable | Immediate (no config needed, but grammar must be bundled) | Requires headers/build system | Requires full project setup | Immediate | Immediate | Immediate |
| "One bad file" safety | ERROR nodes, rest of file parses (but macros can corrupt remainder — see Known Macro Impact) | May cascade within TU; without KeepGoing flag, first missing include stops all parsing | May cascade | Heuristic recovery | Varies | 29% failure rate |
| License compatibility | MIT | Apache 2.0 + LLVM Exceptions | Apache 2.0 + LLVM Exceptions | GPL-2.0+ (incompatible) | GPL (incompatible) | BSD |

---

## C++20 Module Adoption

C++20 modules are the least-adopted major C++20 feature. This matters because modules change the compilation model fundamentally — `import` replaces `#include`, and module interface units (`.cppm`, `.ixx`) are a new file type any parser must handle.

**Compiler support:** Clang marks the core module papers (P1103R3) as implemented since Clang 15, with refinements through Clang 17. However, header units are "highly experimental," and clangd had no module support until late 2025. GCC supports P1103R3 since GCC 11 with `-fmodules` but lacks private module fragments. MSVC has the most mature implementation (VS 2022 17.4+). All compilers have known bugs with modules in template-heavy code.

> [Clang C++ Status](https://clang.llvm.org/cxx_status.html) — P1103R3 in Clang 15
> [GCC C++ Status](https://gcc.gnu.org/projects/cxx-status.html) — P1103R3 in GCC 11
> [clangd module support](https://chuanqixu9.github.io/c++/2025/12/03/Clangd-support-for-Modules.en.html) — late 2025

**Adoption:** The 2024 Modern C++ DevOps survey found only 29.25% of respondents would even allow module usage — the lowest of any flagship C++20 feature. No major open-source project has fully migrated: Boost maintainers state "the ecosystem is just not ready yet," Qt does not ship module interface units, and LLVM itself does not use modules in its own build. An academic study (Szalay, 2025) found "there is not yet sufficient publicly available experience with significantly complex C++ software projects using modules in the wild."

> [2024 Modern C++ DevOps Survey](https://moderncppdevops.com/2024-survey-results/) — 29.25% adoption
> [Boost issue #1023](https://github.com/boostorg/boost/issues/1023) — "ecosystem not ready"
> [Refactoring to Standard C++20 Modules (Szalay, 2025)](https://onlinelibrary.wiley.com/doi/full/10.1002/smr.2736)

**Build system support:** CMake 3.28+ officially supports C++20 named modules with Clang 16+/GCC 14+ (Ninja generator) and Visual Studio 2022 generators. This was the key missing piece for years.

> [import CMake; the Experiment is Over!](https://www.kitware.com/import-cmake-the-experiment-is-over/) — CMake 3.28

**Parser support:** tree-sitter-cpp has module syntax support on master (Feb 2025) but not in any released version. libclang parses module syntax as part of the Clang frontend. For RepoQL, the practical impact is low today — very few codebases being indexed will use modules — but a new format loader should at minimum not crash on module syntax.

---

## Gaps

Findings from this research resolved several initially unknown items. Remaining gaps:

- **TreeSitter.DotNet C++ grammar bundling:** Confirmed: TreeSitter.DotNet does **not** bundle C++ (or even a current C grammar). Adding C++ requires building the grammar from source and either contributing upstream or bundling separately. The effort to do this is unknown.
- **Quantitative tree-sitter C++ error rates:** Qualitative data now exists (see Known Macro Impact) but no formal benchmark measures what percentage of files in a given project parse without ERROR nodes. This matters for the "trustworthy or loudly not" promise.
- **libclang AST completeness without headers:** The degradation mechanism is now understood (see libclang Requirements section) but no benchmark measures the practical utility: "if I parse a `.cpp` file with no includes available, how many of its functions and classes appear in the AST?"
- **Hybrid approach feasibility:** Using tree-sitter as the fast default with optional libclang enrichment when a compilation database is available. No existing implementation to reference.
- **RepoQL.Grammar (ANTLR/Pidgin):** The project has a grammar framework. Whether this could host a C/C++ grammar as an alternative to tree-sitter or libclang was not evaluated.
- **CppAst.NET without headers:** CppAst.NET wraps libclang and surfaces errors through `CppCompilation.Diagnostics`. Without configured include folders, it produces clang errors. How useful the partial output is for structural extraction is not benchmarked.

---

## Validation Experiments

To resolve the remaining gaps before committing to an approach:

**1. Tree-sitter grammar bundling (1-2 hours)**
Build tree-sitter-cpp from the official repository's master branch (which includes C++20 module syntax) and integrate it into a test project using the TreeSitter.DotNet runtime. Verify that the Ruby loader pattern works with the C++ grammar. This determines whether the TreeSitter.DotNet runtime can load externally-built grammars without forking the package.

**2. Tree-sitter parse quality on representative codebases (2-4 hours)**
Parse a selection of real-world C++ files through tree-sitter-cpp and count ERROR/MISSING nodes. Candidate codebases: one Qt-based project (macro-heavy), one header-only library (Boost.Asio or similar), one game engine sample (Unreal or Godot), one "clean" modern C++ project (e.g., a recent open-source tool). Measure: files with zero errors, files with errors that don't affect the rest of the parse, files where errors cascade. This directly answers whether tree-sitter's output is "trustworthy or loudly not."

**3. libclang degraded mode quality (2-4 hours)**
Parse the same file set with CppAst.NET or ClangSharp, once with full headers and once without. Compare: how many functions, classes, and type definitions appear in the degraded AST vs the complete AST? This answers whether libclang's partial results are useful enough to annotate the graph, or whether they'd violate the trustworthiness promise.

**4. Hybrid prototype (4-8 hours, only if experiments 1-3 are promising)**
Build a minimal format loader that uses tree-sitter for fast structural extraction, then optionally enriches with libclang type information when `compile_commands.json` is present. Measure: what additional edges (type references, template instantiations) does libclang provide beyond tree-sitter? Is the enrichment worth the complexity?

---

## Source Incentives

| Source | Incentive |
|--------|-----------|
| tree-sitter project | Open source, editor-focused. Incentive: adoption in editors. Not positioned as a compiler replacement. |
| ClangSharp / LLVM | Microsoft-backed (.NET Foundation). Incentive: enabling .NET/native interop tooling. |
| CppAst.NET | Individual maintainer (xoofx). Incentive: PInvoke generation for game engines. |
| GitHub (code navigation) | Commercial platform. Incentive: justifying tree-sitter investment while being honest about C++ challenges. |
| Jake Zimmerman (tree-sitter limitations) | Individual developer. Incentive: technical accuracy. No commercial stake. |
| Trail of Bits (regex limitations) | Security consulting firm. Incentive: demonstrating static analysis capabilities. |
| Cristian Adam (libclang benchmarks) | Qt developer. Incentive: improving Qt Creator performance. Detailed, reproducible benchmarks. |
