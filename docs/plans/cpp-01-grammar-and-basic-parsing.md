---
description: Plan for C/C++ format loader — grammar build/bundle, classification, core structure extraction, and x-ray generation
tags: [format, cpp, c, plan, grammar, tree-sitter, parsing]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: C/C++ Loader — Grammar, Classification, and Basic Parsing

Implements: [C/C++ Format Design](../designs/future/cpp-format-loader.md) — Grammar Management, Classification, Materialization, Edge Types and Node Kinds, X-Ray Templates, Project Structure

## Scope

**Covers:**
- New project `RepoQL.Formats.Cpp` with DI registration
- tree-sitter-cpp grammar build from source, native library bundling for all platforms
- Validation experiment #1: confirm TreeSitter.DotNet loads externally-built grammars
- Validation experiment #2: measure parse quality on real C++ codebases (Qt, Boost, Linux kernel headers)
- `CppClassifier` pipeline processor — extension matching and `.h` content sniffing
- `CppTreeSitterClient` — thread-safe grammar loading and parsing
- `CppParser` — pipeline processor entry point (analogous to `RubyParser`)
- `CppMaterializer` — CST walk producing Records for core node types
- `CppXRayGenerator` — headline, summary, structure templates
- `CppNodeKinds` and `CppEdgeTypes` constants
- Core node types: `document`, `cpp.type`, `cpp.member`, `cpp.function`, `cpp.namespace`
- `HAS_PART` composition edges
- Qualified name computation (namespace stack + class stack)
- Access specifier tracking (`public`/`private`/`protected` as properties)
- Tests for grammar loading, classification, parsing, and x-ray generation

**Does not cover:**
- Macro interference detection (Plan: cpp-02-error-handling-and-analysis)
- ERROR/MISSING node classification (Plan: cpp-02-error-handling-and-analysis)
- Single-file analysis — includes, doc comments, attributes, test detection (Plan: cpp-02-error-handling-and-analysis)
- `cpp.include`, `cpp.macro`, `cpp.using` node types (Plan: cpp-02-error-handling-and-analysis)
- Template parameter extraction (Plan: cpp-02-error-handling-and-analysis)
- Multi-file analysis — header/source linking, inheritance edges, transitive includes (Plan: cpp-03-cross-file-intelligence)
- SQL views (Plan: cpp-03-cross-file-intelligence)
- `help://` documentation (Plan: cpp-03-cross-file-intelligence)

## Enables

Once this exists:
- **Agents can discover C/C++ files** — `explore` finds `.cpp`, `.h`, `.hpp` files with meaningful headlines showing classes, functions, namespaces
- **Agents can see C/C++ structure** — `read` returns class hierarchies, function signatures, namespace organization without opening files
- **C/C++ types appear in the shared `Types` view** — `cpp.type` nodes match `WHERE kind LIKE '%.type'`
- **Parse quality is validated** — experiments #1 and #2 answer whether tree-sitter-cpp works for real codebases before investing in analysis layers
- **Plans 02 and 03 can proceed** — both build on the grammar, classifier, materializer, and node types established here

This is the foundation and the highest-risk increment. Grammar loading must work before any C/C++ features exist.

## Prerequisites

- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — already in codebase via `RepoQL.Formats.Ruby`
- [tree-sitter-cpp](https://github.com/tree-sitter/tree-sitter-cpp) grammar source — MIT license. Build from pinned master commit (post Feb 2025 for C++20 module syntax)
- C compiler toolchain for grammar builds — `cc` on Linux/macOS, MSVC or MinGW on Windows
- `LiquidTemplateRenderer` and `StandardFilters` from `RepoQL.Templating`
- `TokenEstimator` from `RepoQL.Contracts`
- Pipeline infrastructure: `IAsyncPipeline`, `FormatDescriptor`, `AddIndexingProcessor`
- Ruby tree-sitter integration (`src/Formats/RepoQL.Formats.Ruby/TreeSitter/`) as reference pattern

## North Star

Index a 3,000-file C++ project and see every class, struct, function, and namespace from headlines and structure views — without opening a single file, without a build system, without any configuration. A `.cpp` file should be as structurally informative in explore results as a `.cs` file.

## Done Criteria

### Validation Experiments

- When TreeSitter.DotNet is given an externally-built `tree-sitter-cpp` native library, it shall load the grammar and parse C++ source text into a syntax tree (Experiment #1)
  - If loading fails, evaluate `TreeSitter.Bindings` as fallback; document findings
- When tree-sitter-cpp parses files from Qt, Boost, and Linux kernel headers, the ERROR node contamination rate shall be measured per file (Experiment #2)
  - If >30% of files have ERROR nodes contaminating >50% of extractable structure, re-evaluate the approach
  - Results shall be documented with file counts, error rates, and representative failure examples

### Project Structure

- The project `RepoQL.Formats.Cpp` shall build and be referenced from the solution
- The project shall register its services via `AddCppFormat()` extension method following the Ruby pattern
- The `FormatDescriptor` shall declare supported extensions (`.c`, `.h`, `.cpp`, `.hpp`, `.cc`, `.cxx`, `.hh`, `.hxx`, `.ipp`, `.tpp`, `.inl`) and corresponding media types

### Grammar Management

- The `CppTreeSitterClient` shall load the tree-sitter-cpp native library from the platform-specific `runtimes/` directory
- The `CppTreeSitterClient` shall use `ThreadLocal<Parser>` for thread safety, following `RubyTreeSitterClient`
- When the grammar native library fails to load, the client shall log a diagnostic and return empty results for all parse requests
  - The diagnostic shall be emitted once at startup, not per file
- Grammar native libraries shall be bundled for: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
- The grammar commit SHA shall be recorded in the `.csproj` as metadata

### Classification

- The `CppClassifier` shall assign media types based on file extension:
  - `.c` → `text/plain;kind=code.c`
  - `.cpp`, `.cc`, `.cxx` → `text/plain;kind=code.cpp`
  - `.hpp`, `.hh`, `.hxx` → `text/plain;kind=code.cpp-header`
  - `.ipp`, `.tpp`, `.inl` → `text/plain;kind=code.cpp-inline`
- When the file has a `.h` extension, the classifier shall apply content sniffing:
  1. If a sibling `.cpp`/`.cc`/`.cxx` file with the same stem exists → `code.cpp-header`
  2. If the first 100 lines contain C++ indicators (`class`, `namespace`, `template`, `using namespace`, `#include <iostream>`) → `code.cpp-header`
  3. Otherwise → `code.c`
- When content sniffing encounters an I/O error, the classifier shall fall back to extension-only classification
- When the file extension is not recognized, the classifier shall return `null` (passes to next classifier)

### Parsing

- The `CppMaterializer` shall perform a single depth-first walk of the CST
- The materializer shall NOT build an intermediate AST — it shall transform directly from tree-sitter nodes to Records
- When the parse times out (configurable, default 5 seconds), the parser shall emit an annotation and return partial Records
- When the parser crashes, the parser shall catch, log, and return empty Records with an error annotation

### Core Node Types

- The materializer shall extract `cpp.type` nodes for:
  - `class_specifier` with `kind=class` in properties
  - `struct_specifier` with `kind=struct` in properties
  - `union_specifier` with `kind=union` in properties
  - `enum_specifier` with `kind=enum` and `is_scoped` property (true for `enum class`)
- The materializer shall extract `cpp.member` nodes for:
  - Methods (function definitions inside class/struct bodies) with `kind=method`
  - Constructors with `kind=constructor`
  - Fields with `kind=field`
- The materializer shall extract `cpp.function` nodes for:
  - Free function definitions (outside class/struct bodies) with `kind=function`
- The materializer shall extract `cpp.namespace` nodes for:
  - `namespace_definition` with `kind=namespace`
  - Nested namespaces (`namespace a::b::c`) shall produce nested nodes
  - Anonymous namespaces (`namespace { ... }`) shall produce a node with `name=(anonymous)` and `is_anonymous = "true"`
  - Inline namespaces (`inline namespace v2 { ... }`) shall produce a node with `is_inline = "true"`
- Each non-document node shall have a `HAS_PART` edge from its parent (document, namespace, or class)
- Each `HAS_PART` edge shall have `is_composition = true`

### Properties

- Each node shall have a `name` property with the unqualified name
- Each node shall have a `qualified_name` property with the fully qualified name (`ns::Class::method`)
- Each non-document node shall have a `namespace` property with the enclosing namespace (e.g., `net`, `net::internal`), or absent if at global scope
- Each `cpp.type` node shall have:
  - `extends` — comma-separated base class names (when present)
  - `accessibility` — default access (`public` for struct, `private` for class)
  - `is_abstract` — `"true"` when the type has at least one pure virtual method (computed during materialization walk)
  - `is_forward_declaration` — `"true"` for forward declarations (`class Foo;` without body)
- Each `cpp.member` node shall have:
  - `accessibility` — `public`, `private`, or `protected`
  - `declaring_type` — the unqualified name of the containing class/struct
  - `return_type` — the return type as written (for methods)
  - `signature` — the full signature as written (e.g., `void connect(const std::string& endpoint)`)
  - `parameters` — JSON array of parameter objects with `name` and `type` keys
  - `is_virtual`, `is_pure_virtual`, `is_override`, `is_final` — as string `"true"` or absent
  - `is_noexcept`, `is_constexpr`, `is_static`, `is_const` — as string `"true"` or absent
- Each `cpp.function` node shall have:
  - `return_type` — the return type as written
  - `signature` — the full signature as written
  - `parameters` — JSON array of parameter objects with `name` and `type` keys
  - `is_noexcept`, `is_constexpr`, `is_static`, `is_inline` — same convention
- Each `cpp.type` with `kind=enum` shall have:
  - `is_scoped` — `"true"` for `enum class`
  - `underlying_type` — when explicitly specified
  - Enumerators as child `cpp.member` nodes with `kind=enumerator` and `value` property

### State Tracking

- The materializer shall maintain a namespace stack for qualified name computation
- The materializer shall maintain a class/struct stack for member context
- The materializer shall track the current access specifier:
  - `access_specifier` CST nodes change state, not create nodes
  - Default: `private` for `class`, `public` for `struct`
- The materializer shall track current template parameters in state (for future use by Plan 02) but shall NOT emit `is_template` or `template_params` properties in this increment

### X-Ray Generation

- The headline template shall render: filename, media kind, line count, token estimate, primary namespace, top-level declarations (class names, function names)
  - Example: `connection_pool.h | code.cpp-header | 180 ln, ~1.0k tok | ns:net | class ConnectionPool | connect, execute, disconnect`
- The structure template shall render the declaration tree with `+`/`-`/`#` for public/private/protected:
  - Class/struct/union members with signatures
  - Function signatures with return types and qualifiers
  - Namespace nesting
- Templates shall follow the Liquid template pattern from existing format loaders

### Tests

- Test grammar loading on the current platform — verify CST returned for simple C++ source
- Test classification for each extension — verify correct media type
- Test `.h` content sniffing — verify C++ detection for `class`, `namespace`, `template`
- Test `.h` sibling detection — verify promotion to `code.cpp-header` when `.cpp` sibling exists
- Test class extraction — verify `cpp.type` node with members, access specifiers, base classes
- Test struct extraction — verify default public access, fields as members
- Test enum extraction — verify scoped vs unscoped, enumerator values
- Test namespace extraction — verify nested namespaces, qualified names
- Test free function extraction — verify `cpp.function` node with return type and qualifiers
- Test virtual method properties — verify `is_virtual`, `is_pure_virtual`, `is_override`
- Test headline generation — verify format matches exemplar
- Test structure generation — verify `+`/`-`/`#` prefix convention
- Test forward declaration — `class Foo;` produces `cpp.type` with `is_forward_declaration = "true"`
- Test anonymous namespace — verify `name=(anonymous)` and `is_anonymous` property
- Test inline namespace — verify `is_inline` property
- Test `declaring_type` property — method inside class has correct declaring type
- Test `signature` and `parameters` — verify full signature string and JSON parameter array
- Test `namespace` property — verify nodes inside `namespace net { }` have `namespace = "net"`
- Test `.h` content sniffing I/O error — verify fallback to extension-only classification
- Test parse timeout — verify annotation emitted, partial results returned
- Test grammar load failure — verify diagnostic annotation, empty results
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions

## Constraints

- **tree-sitter-cpp only** — no separate tree-sitter-c grammar; C++ grammar handles C files correctly (design decision)
- **No macro detection in this increment** — ERROR nodes will be present but not classified; that's Plan 02
- **No cross-file analysis** — each file parsed independently; header/source linking is Plan 03
- **No SQL views** — nodes are queryable via the shared Types/Functions views; C++ specific views are Plan 03
- **Follow Ruby tree-sitter patterns** — `CppTreeSitterClient` mirrors `RubyTreeSitterClient` for grammar loading, thread safety, and parser lifecycle
- **Grammar pinned to commit SHA** — not latest, not tag; explicit, tested, reproducible
- **Build from master, not release** — v0.23.4 lacks C++20 module syntax; master has it (design decision)

## References

- [C/C++ Format Design](../designs/future/cpp-format-loader.md) — architecture, grammar management, classification, materialization
- [C/C++ Format North Star](../north-star/formats/cpp.md) — what great looks like
- [C/C++ Parsing Research](../research/cpp-parsing-options.md) — tree-sitter evaluation, validation experiments, macro interference analysis
- [C/C++ Indexing Flow](../flows/future/cpp-indexing.md) — pipeline stages, CST-to-graph mapping table
- Ruby tree-sitter integration (`src/Formats/RepoQL.Formats.Ruby/TreeSitter/`) — `RubyTreeSitterClient`, `ThreadLocal<Parser>` pattern
- [tree-sitter-cpp](https://github.com/tree-sitter/tree-sitter-cpp) — MIT license, grammar source
- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — runtime already in codebase
- [Processor Guide](../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor implementation patterns
- [Testing Guidelines](../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy conventions

## Error Policy

Errors must not cascade. When parsing fails for a specific file:
1. Log warning with file URI and exception details
2. Emit an annotation with `kind=lint`, `severity=error`, `rule_id=cpp/parse_failure`
3. Return empty Records (file still appears in index with headline "parse failed")
4. Continue processing remaining files

Grammar load failure is a startup-time issue affecting all C/C++ files:
1. Emit one diagnostic annotation at startup
2. All subsequent C/C++ parse requests return empty Records
3. Other format loaders are unaffected

Parse timeout (default 5s) produces partial results — whatever the tree-sitter parser completed before the deadline.
