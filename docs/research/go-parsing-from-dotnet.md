---
description: Research into approaches for parsing Go source code from .NET, informing Go format support in RepoQL
tags: [go, golang, parsing, formats, tree-sitter, antlr]
audience: { human: 50, agent: 50 }
purpose: { research: 90, reference: 10 }
---

# Parsing Go from .NET

Research for the decision of how to add Go format support to RepoQL — specifically, which parsing approach to use for extracting structure (packages, functions, methods, structs, interfaces, relationships) from Go source files within a .NET/C# indexing pipeline.

*Research date: 2026-02-23*

## Context

RepoQL needs a Go format loader following the same pattern as existing loaders. The loader must:

- Extract structural declarations: packages, functions, methods, structs, interfaces, type definitions, constants, variables
- Produce nodes, edges, and spans for the knowledge graph
- Handle malformed files gracefully (errors never cascade)
- Run cross-platform (Windows, Linux, macOS)
- Avoid requiring Go installed on the indexing machine (preferred, not mandatory)

**Existing format loader patterns in RepoQL:**

| Format | Parser | Integration | External dependency |
|--------|--------|-------------|-------------------|
| C# | Roslyn (Microsoft.CodeAnalysis) | NuGet, in-process | None |
| TypeScript | TS Compiler API | Node.js subprocess over stdin/stdout JSON | Node.js runtime |
| PHP | ANTLR4 (grammar compiled to C#) | NuGet, in-process | None |
| Ruby | TreeSitter.DotNet | NuGet, in-process | None |

**Go-specific parsing considerations:**

Go is simpler to parse than Ruby or C# in most respects — no operator overloading, no macros, no ambiguous `/` operator, mandatory braces. However, it has unique structural features that a knowledge graph must capture:

- **Implicit interface implementation** — Go types implement interfaces by having the right method set, with no `implements` keyword. This relationship must be computed, not declared.
- **Struct embedding** — Composition via embedded fields promotes methods and fields to the outer struct, affecting interface satisfaction.
- **Visibility by casing** — Uppercase = exported, lowercase = unexported. No `public`/`private` keywords.
- **Multiple return values** — Idiomatic `(value, error)` pattern. Functions routinely return 2+ values.
- **`iota` enum pattern** — Go has no enum type. The convention is `const` blocks with `iota` and a named type.
- **Compiler directives** — `//go:build`, `//go:embed`, `//go:generate` appear as comments but carry semantic meaning.
- **go.mod** — Module metadata, dependencies, Go version constraint. Separate parse target from `.go` files.
- **Test conventions** — `_test.go` files with `TestXxx`, `BenchmarkXxx`, `ExampleXxx`, `FuzzXxx` naming patterns.

---

## Tree-Sitter via TreeSitter.DotNet

The [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) NuGet package (v1.3.0, January 2026) provides .NET bindings to tree-sitter with 28+ bundled language grammars, **including Go**. All native binaries for win-x64, win-x86, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64 ship inside the single NuGet package.

> [TreeSitter.DotNet on NuGet](https://www.nuget.org/packages/TreeSitter.DotNet) — 14.1K downloads, MIT license

The underlying [tree-sitter-go](https://github.com/tree-sitter/tree-sitter-go) grammar is under the official tree-sitter organization. ~400 stars, 400+ commits, 25 contributors, v0.25.0 (August 2025). Used by GitHub for code navigation, Neovim, Helix, Zed, and ~1,200 dependents. (Stats as of February 2026.)

> [tree-sitter-go on GitHub](https://github.com/tree-sitter/tree-sitter-go) — official grammar, production-grade

**Generics support:** Added in v0.20.0 (August 2023). Includes `type_parameter_declaration`, `generic_type`, and type arguments in call expressions. Current with Go 1.21+ features.

API style:

```csharp
using var language = new Language("Go");
using var parser = new Parser(language);
using var tree = parser.Parse(goSourceCode);

using var query = new Query(language, "(function_declaration name: (identifier) @func_name)");
foreach (var capture in query.Execute(tree.RootNode).Captures)
    Console.WriteLine($"Found function: {capture.Node.Text}");
```

Tree-sitter has built-in error recovery: invalid syntax produces `ERROR` nodes in the tree while the rest parses normally. This aligns with RepoQL's "errors never cascade" constraint.

**RepoQL precedent:** Already in use. `RepoQL.Formats.Ruby` references `TreeSitter.DotNet`. The pattern is proven — a Go format loader would follow the same architecture.

**Risks:** TreeSitter.DotNet has ~12 GitHub stars and a single maintainer (mariusgreuel). Small project, single-point-of-failure risk. Tree-sitter parsers are not thread-safe — each thread needs its own Parser instance.

Other .NET tree-sitter bindings exist but do not currently bundle a Go grammar:

| Package | Go? | Status | Notes |
|---------|-----|--------|-------|
| [csharp-tree-sitter](https://github.com/tree-sitter/csharp-tree-sitter) (official) | No | Early stage, Windows-only | 118 stars, TODO-laden README |
| [dotnet-tree-sitter](https://github.com/profMagija/dotnet-tree-sitter) | No | 11 commits, no releases | Too immature |
| [TreeSitter.Bindings](https://www.nuget.org/packages/TreeSitter.Bindings/0.0.2) | Unclear | 0.0.2, 172 downloads | Not enough traction |

> [mariusgreuel/tree-sitter-dotnet-bindings](https://github.com/mariusgreuel/tree-sitter-dotnet-bindings) — TreeSitter.DotNet source, MIT license

---

## ANTLR4

ANTLR4 has excellent C# target support and is already used by RepoQL for PHP parsing. A Go grammar exists in the official grammars-v4 repository.

**The grammar:**

| Attribute | Value |
|-----------|-------|
| Repository | [antlr/grammars-v4/golang](https://github.com/antlr/grammars-v4/tree/master/golang) |
| Grammar files | `GoLexer.g4` + `GoParser.g4` |
| C# target | Yes — `CSharp/GoParserBase.cs` included |
| License | BSD-3-Clause |
| Grammar size | ~541 lines |

> [GoParser.g4](https://github.com/antlr/grammars-v4/blob/master/golang/GoParser.g4) — the grammar file
> [GoParserBase.cs](https://github.com/antlr/grammars-v4/blob/master/golang/CSharp/GoParserBase.cs) — C# parser base class

**Generics support:** Yes — grammar includes `typeParameters`, `typeParameterDecl`, `typeElement`, `typeTerm` rules with `UNDERLYING?` modifier for the `~T` approximation syntax.

**Coverage:** Package/import declarations, function/method declarations with receivers, variable/const/type declarations, all statement types, full expression parsing, type literals (arrays, structs, pointers, functions, interfaces, slices, maps, channels), composite literals, type assertions, conversions. Spec basis is golang.org/ref/spec.

**RepoQL precedent:** Already in use. `RepoQL.Formats.PHP` demonstrates the exact pattern: include `.g4` files, set `<Visitor>true</Visitor>`, implement a visitor. RepoQL already ships ANTLR-based loaders directly in format projects.

Integration pattern (from PHP):

```xml
<PackageReference Include="Antlr4.Runtime.Standard" />
<PackageReference Include="Antlr4BuildTasks" PrivateAssets="all" />
<Antlr4 Include="Grammar\GoLexer.g4">
  <Listener>false</Listener>
  <Visitor>true</Visitor>
</Antlr4>
<Antlr4 Include="Grammar\GoParser.g4">
  <Listener>false</Listener>
  <Visitor>true</Visitor>
</Antlr4>
```

**Trade-off vs tree-sitter:** ANTLR gives a richer AST with named rules and typed visitors — semantic extraction is more ergonomic. Tree-sitter gives a CST (concrete syntax tree) with named node types — lighter-weight but less structured. ANTLR has error recovery strategies but produces lower-quality recovery on malformed input; tree-sitter is designed for incremental parsing of incomplete code and produces partial parses with `ERROR` nodes while preserving the rest of the tree.

> [Antlr4.Runtime.Standard on NuGet](https://www.nuget.org/packages/Antlr4.Runtime.Standard) — ANTLR4 C# runtime

---

## Native Go Toolchain

Go's own `go/parser` and `go/ast` packages provide 100% fidelity parsing. Two integration paths from .NET:

### Shell out to asty (Go AST → JSON)

[asty](https://github.com/asty-org/asty) converts Go AST to/from JSON. 87 stars, Apache-2.0, active maintenance, listed in "Awesome Go".

> [asty-org/asty on GitHub](https://github.com/asty-org/asty) — Go AST ↔ JSON, active

```bash
asty go2json -input file.go -output ast.json
```

Output mirrors Go's `go/ast` structs exactly, including `TypeParams` for generics. Docker image available (`astyorg/asty`).

**Integration pattern:** Shell out to `asty go2json`, deserialize JSON AST into C# types, walk the structure. Similar to how RepoQL's TypeScript loader shells out to Node.js.

**Persistent subprocess option:** Spawn once, pipe files over stdin/stdout. Same architecture as TypeScript loader. Avoids per-file process spawn overhead.

**Older alternatives** (not recommended):
- [goblin](https://github.com/ReconfigureIO/goblin) — 42 stars, last commit January 2018. No recent activity.
- [go-symbols](https://github.com/newhook/go-symbols) — 27 stars, last commit 2015. No recent activity.

### CGO shared library + P/Invoke

Build a Go shared library wrapping `go/parser`, export C functions, P/Invoke from .NET.

```bash
go build -buildmode=c-shared -o goparser.dll ./cmd/parser
```

> [golang/go#26714](https://github.com/golang/go/issues/26714) — Windows DLL runtime initialization issues with CGO

Known issues with CGO on Windows. Complex build pipeline. Cross-FFI memory management is fragile. Not used by RepoQL for anything else.

> [vladimirvivien/go-cshared-examples](https://github.com/vladimirvivien/go-cshared-examples) — CGO shared library examples

---

## gopls (Language Server Protocol)

[gopls](https://go.dev/gopls/) is Go's official language server. Part of [golang/tools](https://github.com/golang/tools), BSD-3-Clause.

> [go.dev/gopls](https://go.dev/gopls/) — official Go language server

Relevant LSP capabilities:
- `textDocument/documentSymbol` — hierarchical symbols per file
- `workspace/symbol` — search across workspace
- `textDocument/hover` — type information at any position
- `textDocument/definition` — cross-reference resolution

**Experimental MCP support:** `gopls -mcp.listen` exposes MCP tools via SSE. Could theoretically integrate via RepoQL's MCP client system.

**Limitations:**
- Requires Go toolchain installed and a valid Go module (`go.mod`)
- Heavyweight — starts a full language server process
- Slow startup for large workspaces
- Designed for editor integration, not batch processing
- Known bugs in `documentSymbol` selection ranges ([golang/go#73521](https://github.com/golang/go/issues/73521))

**When it makes sense:** As a supplement for semantic information beyond syntax — resolved types, cross-package references, interface satisfaction computation. High integration cost and external runtime dependency make it impractical as a primary parsing approach for RepoQL's constraints (cross-platform, no mandatory external runtimes).

---

## Other Approaches Considered

**go2cs** ([GridProtectionAlliance/go2cs](https://github.com/GridProtectionAlliance/go2cs)) — Go-to-C# transpiler. 392 stars, MIT, last commit February 2022. Uses the same ANTLR grammar from grammars-v4, so using ANTLR directly is simpler. Not a parsing library.

**ast-grep** ([ast-grep/ast-grep](https://github.com/ast-grep/ast-grep)) — Structural code search built on tree-sitter. Rust CLI, no .NET library API. Not suitable as a parser.

**Pidgin parser combinators** — possible in theory, but hand-writing a Go parser with combinators would be a massive undertaking when grammars already exist. Not practical.

**`go/packages` + `go/types` (Go semantic analysis)** — Go's `go/packages` package loads typed, resolved package information including cross-package references and computed interface satisfaction. This is the authoritative way to determine which types implement which interfaces. Accessible only from Go code — same integration story as asty (shell out or CGO). Relevant if RepoQL needs semantic-level accuracy beyond what syntax parsing provides.

> [go/packages documentation](https://pkg.go.dev/golang.org/x/tools/go/packages) — typed package loading
> [go/types documentation](https://pkg.go.dev/go/types) — type checking and interface satisfaction

---

## Go Structural Elements for the Graph

What a Go format loader needs to extract, mapped to RepoQL's graph model.

### Node Kinds

Two approaches to modeling, depending on alignment with existing loaders:

**Normalized (matches C# pattern):** Fewer node kinds, distinguish via `kind` property.

| Node Kind | Go Constructs | `kind` Property Values |
|-----------|--------------|----------------------|
| `go.package` | `package` declaration | — |
| `go.type` | struct, interface, type def, type alias | struct, interface, type_definition, type_alias |
| `go.member` | function, method, constant, variable, field | function, method, constant, variable, field |
| `go.import` | `import` | — |

This mirrors C#'s `csharp.type` and `csharp.member` pattern with `kind` property discrimination. Enables shared views and consistent SQL patterns across languages.

**Fine-grained (maximizes Go expressiveness):**

| Node Kind | Go Construct | Key Properties |
|-----------|-------------|----------------|
| `go.package` | `package` declaration | name, path, is_main |
| `go.function` | `func` (no receiver) | name, exported, signature, variadic, type_params |
| `go.method` | `func` (with receiver) | name, exported, signature, receiver_type, pointer_receiver, type_params |
| `go.struct` | `type X struct` | name, exported, type_params |
| `go.interface` | `type X interface` | name, exported, type_params, has_type_constraints |
| `go.type_definition` | `type X underlying` | name, exported, underlying_type, type_params |
| `go.type_alias` | `type X = Y` | name, exported, aliased_type |
| `go.constant` | `const` | name, exported, type, value, iota_position |
| `go.variable` | `var` | name, exported, type, initial_value |
| `go.field` | struct field | name, type, tag, exported, is_embedded |
| `go.import` | `import` | path, alias, is_dot, is_blank |

The normalized approach is more consistent with existing loaders. The fine-grained approach better represents Go's distinct concepts. This is a design decision for the format loader, not a parsing decision.

### Edge Types

| Edge Type | From → To | Notes |
|-----------|-----------|-------|
| `HAS_PART` | document → package → type → member | Standard composition hierarchy |
| `IMPORTS` | file → package | With alias metadata |
| `EMBEDS` | struct → type | Composition; promotes methods and fields |
| `IMPLEMENTS` | type → interface | **Computed** from method sets (see below) |
| `CALLS` | function/method → function/method | Call graph (phase 2) |
| `USES_SYMBOL` | any → any | Reference tracking (phase 2) |

### Implicit Interface Implementation

The most important Go-specific relationship. Rules:

- Method set of `T` = methods with value receiver `T`
- Method set of `*T` = methods with receiver `T` or `*T`
- `T` implements interface `I` if `T`'s method set is a superset of `I`'s method set
- `*T` may implement an interface that `T` does not (pointer receivers)
- Struct embedding promotes methods, changing the method set

Computing this relationship requires cross-file analysis — a type's methods may be defined across multiple files in the same package. This fits RepoQL's idle-processing phase (multi-file analysis after hot-path indexing).

> [Go Language Specification — Method sets](https://go.dev/ref/spec#Method_sets)
> [Go Language Specification — Interface types](https://go.dev/ref/spec#Interface_types)

### Annotations

| Annotation Kind | Source | Properties |
|-----------------|--------|------------|
| `go.build_constraint` | `//go:build` tags, filename patterns | constraint_expression |
| `go.generate` | `//go:generate` directives | command |
| `go.embed` | `//go:embed` directives | patterns |
| `go.enum_block` | `const` block with `iota` + named type | type_name, member_count |
| `go.test` | `_test.go` functions | test_kind (test/benchmark/example/fuzz) |

### go.mod Support

Separate from `.go` file parsing. A `go.mod` parser should extract:

| Element | Graph Representation |
|---------|---------------------|
| Module path | Property on repository/root node |
| Go version | Annotation on module |
| Direct dependencies | `DEPENDS_ON` edges |
| Indirect dependencies | `DEPENDS_ON` edges (marked indirect) |
| Replace directives | Annotation with replacement path |

go.mod uses a simple, well-specified format. It could be parsed with parser combinators or simple line-based parsing.

**go.work (workspace mode):** Go 1.18+ supports multi-module workspaces via `go.work` files. These declare which local modules are part of a workspace, replacing `replace` directives during development. A `go.work` parser would capture workspace membership and local module relationships.

> [Go Modules Reference](https://go.dev/ref/mod) — official spec
> [go.mod file reference](https://go.dev/doc/modules/gomod-ref) — directive reference
> [go.work reference](https://go.dev/ref/mod#workspaces) — workspace mode

### Project Structure Conventions

| Convention | Parser Action |
|------------|--------------|
| `cmd/*/main.go` | Mark as entry point |
| `internal/` | Annotate import restriction |
| `_test.go` suffix | Separate test graph from production graph |
| `testdata/` | Skip indexing |
| `vendor/` | Skip or index as dependencies |
| `*_linux.go`, `*_amd64.go` | Detect as build-constrained by filename |

> [Organizing a Go module (official)](https://go.dev/doc/modules/layout)
> [Standard Go Project Layout](https://github.com/golang-standards/project-layout)

---

## Performance

No published benchmarks for tree-sitter-go specifically. Extrapolated from other grammars and Go-specific context:

Go files are typically modest in size — the Go community strongly favors short files and short functions. The [Go Code Review Comments](https://go.dev/wiki/CodeReviewComments) guide and `gofmt` tooling enforce this culture.

**Tree-sitter cross-language extrapolation:**

| Grammar | File size | Parse time | Source |
|---------|----------|------------|--------|
| Scala (5,835 lines) | ~150KB | 73ms | [eed3si9n](https://eed3si9n.com/fast-scala3-parsing-with-tree-sitter/) |
| Rust (2,157 lines) | ~64KB | 6.48ms | [tree-sitter docs](https://tree-sitter.github.io/tree-sitter/using-parsers/) |
| Python (20,000 lines) | ~500KB | ~120ms | [tree-sitter discussion #3413](https://github.com/tree-sitter/tree-sitter/discussions/3413) |

Typical Go files (< 500 lines): sub-millisecond to low single-digit milliseconds.

**ANTLR4:** Generates LL(*) parsers. For Go files of typical size, performance is more than adequate. Already proven at scale in RepoQL's PHP parser.

**asty subprocess:** One-time Go runtime startup (~50-100ms), then sub-millisecond parse per file if using persistent subprocess. Per-file process spawn would be ~50-100ms overhead per file.

**Projected batch performance (10,000 files):**

| Approach | Estimated total time | Bottleneck |
|----------|---------------------|------------|
| Tree-sitter (in-process) | ~1-3s | Parse throughput |
| ANTLR4 (in-process) | ~2-5s | Parse throughput |
| asty (persistent subprocess) | ~3-5s | IPC serialization |
| asty (per-file subprocess) | ~8-17 min | Process spawn per file |
| gopls | Minutes | Server startup, LSP round-trips |

All estimates are extrapolated — no Go-specific benchmarks exist for any approach. Given typical Go file sizes and the performance profile of other tree-sitter/ANTLR grammars, parsing is unlikely to be the bottleneck for batch indexing, but this should be validated with hands-on measurement before committing to an approach.

---

## Comparison

| Dimension | Tree-Sitter (TreeSitter.DotNet) | ANTLR4 | Go Toolchain (asty) | gopls (LSP) |
|-----------|:---:|:---:|:---:|:---:|
| **Already in RepoQL** | Yes (Ruby) | Yes (PHP, CSS, Terraform) | No (but TypeScript uses similar shell-out) | No |
| **Go generics support** | Yes (v0.20.0+) | Yes | Yes (native) | Yes (native) |
| **NuGet package** | TreeSitter.DotNet | Antlr4.Runtime.Standard | N/A | N/A |
| **Go grammar ready** | Bundled | Copy from grammars-v4 | N/A | N/A |
| **C# integration** | NuGet, in-process | NuGet, in-process | JSON deserialization | LSP client needed |
| **External runtime** | None | None | Go toolchain | Go toolchain |
| **Error tolerance** | Excellent (partial parse) | Limited (recovery strategies, degraded fidelity) | Limited (Go parser returns `Bad*` nodes for errors) | Good |
| **Fidelity to Go spec** | Very high | High | Perfect | Perfect |
| **AST richness** | CST with named nodes | Full AST with typed visitors | Full Go AST (JSON) | Symbols only |
| **Performance (10K files)** | ~1-3s | ~2-5s | ~3-5s (persistent) | Minutes |
| **Maintenance** | Active (grammar + bindings) | Active (grammars-v4) | Active (asty) | Active (gopls) |
| **License** | MIT | BSD-3 | Apache-2.0 | BSD-3 |
| **Setup complexity** | Trivial | Low | Medium | High |
| **Implementation effort** | Low (follow Ruby pattern) | Low (follow PHP pattern) | Medium (JSON schema + deserializer) | High |

---

## Gaps

- **Tree-sitter-go parse speed** — no published benchmarks for the Go grammar specifically. Estimates are extrapolated from other grammars. Grammar quality significantly affects performance (52.8x range observed across grammars).
- **TreeSitter.DotNet real-world usage** — ~12 GitHub stars, single maintainer. Limited community validation beyond RepoQL's own Ruby usage. Would benefit from hands-on testing.
- **ANTLR Go grammar completeness** — the grammars-v4 Go grammar claims spec compliance but has no published test suite or coverage report. Unknown edge case behavior.
- **ANTLR Go grammar maintenance** — grammars-v4 is community-maintained. Unknown if the Go grammar tracks language changes promptly (e.g., Go 1.22 range-over-func, Go 1.24 generic type aliases).
- **Implicit interface computation cost** — requires cross-file method set analysis. No data on the computational cost for large Go codebases with many interfaces and types.
- **go.mod parsing complexity** — the format appears simple but has edge cases (replace directives with local paths, retract ranges, toolchain directive in Go 1.21+). No assessment of existing .NET go.mod parsers.
- **Struct tag parsing** — struct tags (`json:"name" db:"column"`) are string literals with their own micro-syntax. Needs a dedicated parser regardless of the primary Go parser choice.
- **`iota` detection reliability** — recognizing `const` blocks as enum patterns requires understanding `iota` increment semantics. Tree-sitter and ANTLR both expose the syntax; the semantic detection logic must be custom regardless.
- **`go/packages` + `go/types` integration** — computing implicit interface satisfaction with full accuracy requires Go's type checker. Syntax-only parsers can approximate (same-package method set matching) but cannot resolve cross-package types or handle complex embedding chains. No assessment of the accuracy gap between syntax-level and semantic-level interface detection.
- **`go.work` workspace support** — multi-module workspace metadata. No assessment of prevalence in real-world Go projects or parsing complexity.
- **ANTLR error recovery quality** — ANTLR4 has recovery strategies (single-token insertion/deletion, rule resynchronization). The quality of recovery for Go specifically is untested. The claim of "limited" error tolerance needs validation.
- **Go-specific benchmarks** — all performance estimates are extrapolated. No approach has measured Go-specific parse throughput. This should be validated before committing to an approach.

---

## Summary

Three viable approaches, two of which are already proven in RepoQL.

| | Self-contained | Requires Go runtime |
|---|---|---|
| **Full parse** | Tree-sitter (NuGet), ANTLR4 (grammar) | asty (process), gopls (LSP) |

Go is significantly easier to parse than Ruby — no ambiguous operators, no metaprogramming that creates symbols dynamically, mandatory braces, simple grammar. Both tree-sitter and ANTLR4 are well-suited. The unique challenge is not parsing but the semantic layer: computing implicit interface satisfaction from method sets across files.

| Approach | Integration story | Error tolerance | Implementation effort |
|----------|------------------|-----------------|----------------------|
| Tree-sitter | Follow Ruby pattern | Excellent | Low |
| ANTLR4 | Follow PHP pattern | Limited | Low |
| asty | Follow TypeScript pattern | Limited | Medium |
| gopls | New pattern | Good | High |

Speed is unlikely to be a differentiator — extrapolated estimates suggest all in-process approaches handle 10,000 files in seconds, pending validation. The key trade-off is between tree-sitter's error tolerance (partial parses of broken files) and ANTLR4's richer AST (typed visitors, cleaner semantic extraction). Both are already dependencies. Both have active Go grammars. Both support generics.
