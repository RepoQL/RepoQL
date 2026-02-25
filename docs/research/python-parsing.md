# Python Parsing Research

Research for deciding how to parse and support Python files in RepoQL's format system.

*Research date: 2026-02-23*

## Context

RepoQL needs to index Python codebases into its knowledge graph — extracting classes, functions, imports, decorators, type hints, docstrings, and their relationships. Python is the most-requested language not yet supported.

**Decision:** Which parsing technology to use for `RepoQL.Formats.Python`.

**Constraints:**
- Must run on a developer laptop without cloud dependencies
- Must handle Python 3.10+ syntax (match statements, union types) and ideally 3.12+ (type parameter syntax, `type` aliases)
- Must handle indentation-sensitive grammar correctly
- Must integrate with the existing format system (`IFormatLoader` → `DocumentModel` → `Records`)
- One bad file must never break anything else

**Existing precedents in-codebase:**
- **Ruby** — uses `TreeSitter.DotNet` (tree-sitter via native P/Invoke). In-process, thread-safe, S-expression queries. ~850 lines in `RubyTreeSitterClient.cs`.
- **TypeScript/JavaScript** — uses a persistent Node.js child process (`TypeScriptNodeClient.cs`). Line-delimited JSON protocol over stdin/stdout, mutex-serialized (single-flight). Requires Node.js installed.
- **Terraform, CSS, PHP** — use ANTLR4 (`Antlr4.Runtime.Standard` + `Antlr4BuildTasks`). Pure .NET, grammar files compiled at build time.
- **RepoQL.Grammar** — provides `AntlrLanguageBase` (used by Terraform/CSS/PHP) and `PidginLanguageBase` (infrastructure only — no production format uses it yet) integration points.

---

## Tree-sitter via TreeSitter.DotNet

Parse Python using the `tree-sitter-python` grammar through .NET bindings.

| Field | Value |
|-------|-------|
| NuGet package | `TreeSitter.DotNet` |
| Version | 1.3.0 |
| License | MIT |
| Last updated | January 2026 |
| Downloads | ~21K |
| Python grammar | `tree-sitter-python` v0.25.0 (Sept 2025) — bundled in the NuGet package |
| .NET target | .NET Standard 2.0 |
| Native code | Yes — C library via P/Invoke, bundled per-platform |

**Python syntax coverage:**

| Feature | Status |
|---------|--------|
| Match statements (3.10) | Supported — `match_statement`, `case_clause` |
| Type parameters (3.12) | Supported — `type_parameter`, `type_alias_statement` |
| F-strings | Supported |
| Walrus operator (3.8) | Supported |
| Positional-only params (3.8) | Supported |
| Exception groups (3.11) | Supported |
| Decorators, type hints | Full support |

Indentation is handled natively by tree-sitter's external scanner (C code). INDENT/DEDENT tokens are generated correctly without any .NET-side work.

> [tree-sitter-python](https://github.com/tree-sitter/tree-sitter-python) — actively maintained, widely adopted (stats as of 2026-02-23: ~570 commits, ~520 stars, ~1,400 dependent repos)

**Performance:** LR(1) grammar — O(n) parse time. Initial parse in low milliseconds for typical files. Incremental reparse in sub-millisecond. Written in C; .NET calls via P/Invoke.

**Concurrency:** Thread-safe via `ThreadLocal<Parser>` — the exact pattern used by `RubyTreeSitterClient.cs` today.

**Integration pattern** (proven in-codebase):

```csharp
// One-time language load
private static readonly Language SharedLanguage =
    new Language("tree-sitter-python", "tree_sitter_python");

// Per-thread parser
private readonly ThreadLocal<Parser> _parsers =
    new(() => new Parser(SharedLanguage), trackAllValues: true);

// Parse → walk → extract
using var tree = parser.Parse(sourceCode);
var root = tree.RootNode;

// S-expression pattern queries
using var query = SharedLanguage.CreateQuery(
    "(class_definition name: (identifier) @name)");
using var cursor = query.Execute(root);
```

**Key node types for extraction:**

| What to extract | Tree-sitter node |
|-----------------|-----------------|
| Classes | `class_definition` |
| Functions | `function_definition` |
| Async functions | `function_definition` with `async` keyword |
| Decorators | `decorator` |
| Imports | `import_statement`, `import_from_statement` |
| Type hints | `type` (with `generic_type`, `union_type` subtypes) |
| Docstrings | `expression_statement` > `string` (first in body) |
| Match statements | `match_statement`, `case_clause` |
| Type aliases (3.12) | `type_alias_statement` |
| Global/nonlocal | `global_statement`, `nonlocal_statement` |

**Error recovery:** Tree-sitter produces partial parse trees for malformed files — error nodes alongside valid structure. Aligns with the "one bad file never breaks anything" promise.

**Other .NET tree-sitter packages:**

| Package | Status |
|---------|--------|
| `TreeSitter` (profMagija, 1.0.0) | Abandoned since 2019 |
| `tree-sitter` (Summpot, 0.4.19) | Last updated Nov 2023, ~3.5K downloads |
| Official `csharp-tree-sitter` | Exists on GitHub, no NuGet publish |

> [TreeSitter.DotNet on NuGet](https://www.nuget.org/packages/TreeSitter.DotNet) — MIT license, active maintenance

---

## ANTLR4 with grammars-v4 Python Grammar

Parse Python using a generated C# lexer/parser from an ANTLR `.g4` grammar.

| Field | Value |
|-------|-------|
| NuGet package | `Antlr4.Runtime.Standard` 4.13.1 |
| License | BSD 3-Clause |
| Build tool | `Antlr4BuildTasks` (generates C# at build time) |
| .NET target | .NET Standard 2.0, 2.1 |
| Native code | None — pure .NET |

**Available grammars** (from [antlr/grammars-v4](https://github.com/antlr/grammars-v4)):

| Grammar | Python version | Notes |
|---------|---------------|-------|
| `python/python3` | ~3.6 | Basic Python 3, has C# support |
| `python/python3_13` | **3.13.2** | Most current — match statements, type parameters, f-string improvements, soft keywords |

The `python3_13` grammar includes:
- `PythonParser.g4` and `PythonLexer.g4`
- `PythonLexerBase.cs` handling INDENT/DEDENT token generation, encoding tokens, and f-string tokenization
- Based on the official Python PEG grammar (reference `.peg` file included)
- Dedicated tokens for soft keywords

> [ANTLR grammars-v4 python3_13](https://github.com/antlr/grammars-v4/tree/master/python/python3_13) — community-maintained, C# support files included

**Indentation handling:** `PythonLexerBase.cs` maintains an indentation stack and generates synthetic INDENT/DEDENT tokens. This is custom code that ships with the grammar and must be included in the project.

**Performance:** ALL(*) parsing algorithm — worst case O(n⁴), practical performance in milliseconds for typical files. Code generation at build time means no runtime grammar compilation cost.

**Integration pattern** (proven in-codebase for Terraform/CSS/PHP):

```xml
<PackageReference Include="Antlr4.Runtime.Standard" />
<PackageReference Include="Antlr4BuildTasks" PrivateAssets="all" />
<Antlr4 Include="Grammar\PythonLexer.g4">
  <Visitor>true</Visitor>
</Antlr4>
<Antlr4 Include="Grammar\PythonParser.g4">
  <Visitor>true</Visitor>
</Antlr4>
```

Extraction uses a visitor pattern over the generated concrete syntax tree. The existing `AntlrLanguageBase<TLexer, TParser, TRoot>` in `RepoQL.Grammar` provides integration scaffolding.

**Error recovery:** Limited. ANTLR produces error nodes but with less granularity than tree-sitter's partial parse trees.

---

## Pidgin (Parser Combinator)

Write a Python grammar from scratch using the Pidgin combinator library.

| Field | Value |
|-------|-------|
| NuGet package | `Pidgin` 3.5.1 |
| License | MIT |
| Last updated | October 2025 |
| Native code | None — pure .NET |

**Feasibility for Python:** Indentation-sensitive parsing is a fundamental challenge for combinator parsers. Context-free grammars cannot express Python's indentation rules. Solutions require either:
1. A separate lexer pass to inject INDENT/DEDENT tokens, then parse the token stream
2. Stateful combinators threading column position through the parse

No existing Pidgin-based Python grammar exists. Building one from scratch would require implementing the full Python grammar: all statement/expression forms, decorators, type hints, match statements, f-strings (a mini-language), operator precedence, and string escapes.

> [Principled Parsing for Indentation-Sensitive Languages (Adams, 2014)](https://michaeldadams.org/papers/layout_parsing/LayoutParsing.pdf) — academic treatment of the problem

**Effort estimate:** Thousands of lines of combinator code. The indentation problem alone is significant engineering. Practical only for much simpler grammars.

---

## IronPython

Use IronPython's built-in C# parser to parse Python source into a .NET AST.

| Field | Value |
|-------|-------|
| NuGet package | `IronPython` 3.4.2 |
| License | Apache 2.0 |
| Last updated | December 2024 |
| .NET target | .NET 6.0, .NET Standard 2.0, .NET Framework 4.6.2 |
| Native code | None — pure .NET |
| Dependencies | DLR (Dynamic Language Runtime) packages |

**Python version compatibility:** IronPython targets **CPython 3.4** syntax with selected backports. Critical gaps:

| Feature | Status |
|---------|--------|
| Match statements (3.10) | **Not supported** |
| Type parameters (3.12) | **Not supported** |
| Exception groups (3.11) | **Not supported** |
| Walrus operator (3.8) | Not confirmed |
| Positional-only params (3.8) | Not confirmed |

The parser is a hand-written recursive descent parser in C# producing `PythonAst` nodes — walkable .NET objects. However, it carries the full DLR runtime overhead and is designed for execution, not structural extraction.

> [IronPython3 on GitHub](https://github.com/IronLanguages/ironpython3) — targeting CPython 3.4 compatibility

---

## Python.NET (pythonnet)

Embed CPython in-process and use `import ast` directly.

| Field | Value |
|-------|-------|
| NuGet package | `pythonnet` 3.0.5 |
| License | MIT |
| .NET target | .NET Standard 2.0 |
| Requires | Python installation on the machine |

Uses whatever CPython version is installed. If Python 3.12 is present, you get full 3.12 syntax via `ast.parse()`.

**Trade-offs:**
- **GIL contention** — Python's Global Interpreter Lock limits concurrency to one thread at a time
- **Process-level state** — only one Python interpreter per process, complex lifecycle
- **Marshalling overhead** — converting Python AST objects to .NET types crosses an interop boundary
- **External dependency** — requires Python installed, which violates "runs on a developer laptop" if Python is absent

> [pythonnet on GitHub](https://github.com/pythonnet/pythonnet) — MIT license, active

---

## External Process

Spawn `python -c "import ast; ..."` and parse JSON output.

**Approach:** Start a persistent Python child process, send source code via stdin, receive JSON AST via stdout. Similar architectural pattern to `TypeScriptNodeClient.cs` (persistent process, line-delimited JSON). The TypeScript client is mutex-serialized; a Python client could follow the same pattern or use a process pool for concurrency.

**Variant — CST via LibCST or parso:** Instead of `ast.parse()` (which produces an abstract syntax tree that discards formatting/comments), Python libraries like `libcst` and `parso` produce concrete syntax trees preserving whitespace, comments, and exact source positions. These could improve fidelity for docstring extraction and error recovery. Trade-off: heavier Python dependency (`pip install libcst`), but richer structural information.

**Performance:** Process startup ~50-200ms (amortized with persistent process). CPython's parser is highly optimized once running. Can parallelize via multiple Python processes (no GIL issue across processes).

**Trade-offs:**
- Always current, always correct — uses CPython's own parser
- Simple implementation, easy to debug
- Error handling across process boundary adds complexity
- Requires Python installed on the machine
- Process crash from a malformed file affects only that request (recoverable by restarting the process)
- The TypeScript format establishes this pattern, so it's not unprecedented

---

## Other NuGet Packages

| Package | Version | Downloads | Last Updated | Notes |
|---------|---------|-----------|-------------|-------|
| `antlr-parser` | 0.31.2 | ~5K | Nov 2024 | ANTLR wrapper, requires manual visitor per language. Low adoption. |
| `CodeParser` | 2.0.0 | ~2K | Sept 2022 | Claims Python support, uses ANTLR 4.10. No license specified. Likely old grammar. |
| `CSnakes.Runtime` | 1.2.1 | — | — | Embeds CPython via C-API. Has a basic Python parser for extracting function signatures, but not full AST. |
| `Alternet.Studio.Syntax.Parsers.Python` | 10.0.6 | — | — | Commercial IDE component. Requires Language Server. Not suitable for batch indexing. |

All have significant limitations: low adoption, missing licenses, stale grammars, or commercial licensing.

---

## Comparison

| Dimension | Tree-sitter | ANTLR4 | Pidgin | IronPython | pythonnet | External process |
|-----------|-------------|--------|--------|------------|-----------|-----------------|
| Python 3.12+ syntax | Yes | Yes (3.13) | N/A | **No** (3.4) | If installed | If installed |
| Indentation handling | Native (C scanner) | PythonLexerBase.cs | Must build | Built-in | Built-in | Built-in |
| External runtime | None | None | None | None | Python required | Python required |
| Native code | Yes (bundled) | No (pure .NET) | No | No | Yes (Python) | Separate process |
| License | MIT | BSD-3 | MIT | Apache 2.0 | MIT | N/A |
| Parse speed | ~ms (C, O(n)) | ~ms (ALL(*)) | Unknown | Unknown | ~ms | ~ms + IPC |
| Concurrency | ThreadLocal parsers | New parser per thread | Immutable | Thread-safe | GIL-limited | Process-parallel |
| Already in RepoQL | Yes (Ruby) | Yes (Terraform/CSS/PHP) | Base class only (no format) | No | No | Yes (TypeScript) |
| Error recovery | Partial parse trees | Error nodes (limited) | Fail-fast | None | None | None |
| One-file isolation | Natural (parse per file) | Natural (parse per file) | Natural | Natural | GIL serializes | Process crash affects batch |
| Setup friction | NuGet restore only | NuGet restore only | NuGet restore only | NuGet restore only | Python install required | Python install required |
| Diagnostics quality | Error node positions | Error token positions | Parse failure only | Parse failure only | Python traceback | Process stderr |
| Maintenance burden | Low (NuGet updates) | Medium (manual .g4 updates) | Very high | Low | Low | Low |
| Implementation effort | Medium | Medium | Very high | Low (if syntax current) | Medium | Medium |

---

## Python Language Surface

What a Python format handler would extract into the graph:

### Fully Static (from AST alone)

| Element | Graph output |
|---------|-------------|
| `import`, `from...import` | `IMPORTS` edges from module to target |
| `class` definitions | `class` nodes with inheritance edges |
| `def` / `async def` | `function` nodes with parameters, return types |
| Decorators | `DECORATED_BY` edges, semantic annotations (`@property`, `@dataclass`, etc.) |
| Type annotations | `HAS_TYPE` edges on variables, parameters, returns |
| `__init__` self-assignments | Instance variable nodes |
| `__slots__`, `__all__` | Annotations on class/module nodes |
| Docstrings | `docstring` property on module/class/function |
| `global`, `nonlocal` | Scope reference edges |
| Dataclass fields | Field nodes with type, default, factory |
| Enum members | Member nodes with name and value |
| Package structure | `__init__.py` → package node; `__main__.py` → entry point; namespace packages (no `__init__.py`) |
| `.pyi` type stubs | Stub files with type information for untyped code |
| Async structure | `await`, `async for`, `async with` as first-class relationships |

### Partially Extractable (heuristics or cross-module)

| Element | Challenge |
|---------|-----------|
| Relative import resolution | Needs filesystem context for package position |
| `from X import *` | Needs target module's `__all__` |
| Instance variables outside `__init__` | `self.x = ...` in any method — heuristic |
| Old-style type aliases (`X = SomeType`) | Indistinguishable from variable assignment without type inference |
| Protocol conformance | Which classes satisfy a Protocol — needs type checking |
| Constant detection | Convention (`ALL_CAPS`) vs. explicit (`Final`) |

### Not Extractable (runtime only)

`__getattr__` attributes, metaclass-generated members, monkey-patched methods, `exec`/`eval` generated code, dynamic class creation via `type()`.

**Honesty annotations** apply here — the Ruby format emits typed `ruby.metaprogramming` annotations with `rule_id` and `message` payloads (e.g., `rule_id="method_missing"`, `message="method_missing defined, dynamic dispatch possible"`). Python would follow the same pattern with `python.metaprogramming` annotations for `__getattr__`, `exec`, metaclasses, etc.

---

## Gaps

- **Tree-sitter query authoring effort** — the Ruby format has 23+ S-expression queries (in `RubyQueries.cs`) covering its language surface. Python's surface is larger. The total query set hasn't been prototyped.
- **Docstring parsing** — extracting structured information from Google/NumPy/Sphinx docstring formats requires a second parsing pass after AST extraction. No .NET docstring parser was found; this may need custom implementation or a Python-side helper.
- **Framework-specific patterns** — Django (`@app.route`), Flask, FastAPI, pytest decorators carry semantic meaning. How deep to go on framework detection is a scope question, not a parsing question.
- **Python version detection** — files may use syntax from different Python versions. Whether to detect and adapt, or always parse with the latest grammar, hasn't been evaluated.
- **ANTLR python3_13 stability** — the grammar is community-maintained. How reliably it handles edge cases versus the official CPython parser hasn't been tested.
- **tree-sitter-python on Windows** — the `TreeSitter.DotNet` package bundles native libraries per platform, and the Ruby format works on Windows today. But Python-specific edge cases on Windows haven't been verified.

---

## Summary

| | No external runtime | Python 3.12+ | In-codebase precedent | Error recovery | Effort |
|---|---|---|---|---|---|
| **Tree-sitter** | Yes | Yes | Ruby format | Partial parse trees | Medium |
| **ANTLR4** | Yes | Yes (3.13) | Terraform/CSS/PHP | Error nodes | Medium |
| **External process** | No (Python) | If installed | TypeScript format | None (recoverable via restart) | Medium |
| **Pidgin** | Yes | N/A | Base class only | Fail-fast | Very high |
| **IronPython** | Yes | No | None | None | Low |
| **pythonnet** | No (Python) | If installed | None | None | Medium |
