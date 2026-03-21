---
description: Plan for Python format — project scaffolding, TreeSitter.DotNet integration, query-based extraction, surface model, and thread-safety validation
tags: [format, python, tree-sitter, plan, parser]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Python — Tree-Sitter Foundation

Implements: [Python Format Design](../../designs/future/python-format.md) — Tree-Sitter Integration, Surface Model, Project Structure

## Scope

**Covers:**
- New project `RepoQL.Formats.Python` with TreeSitter.DotNet dependency
- New test project `RepoQL.Formats.Python.Tests`
- `PythonTreeSitterClient` — thread-safe wrapper containing all tree-sitter interop
- `PythonQueries` — S-expression query strings for all structural extraction
- `PythonConstants` — node kinds, edge types, property key constants
- Full surface model types in `Surface/` subdirectory
- Thread-safety validation via concurrent parsing tests
- Solution file update (both projects added to `RepoQL.sln`)

**Does not cover:**
- PEP 263 encoding handling (Plan: 02-core-format-loader — file I/O concern)
- Classification or media types (Plan: 02-core-format-loader)
- Materialization to graph nodes/edges (Plan: 02-core-format-loader)
- DI registration or pipeline integration (Plan: 02-core-format-loader)
- SQL views (Plan: 02-core-format-loader)
- X-ray summaries (Plan: 02-core-format-loader)
- Metaprogramming and framework annotations (Plan: 03-annotations-documentation)
- `help://` documentation (Plan: 03-annotations-documentation)

## Enables

Once this exists:
- **Risk is retired** — TreeSitter.DotNet loads the Python grammar, parses real files, handles errors, and is thread-safe. The single riskiest technical choice is validated before any downstream code is written
- **Plan 02 can proceed** — the core format loader consumes `PythonTreeSitterClient` directly
- **Query coverage is known** — the S-expression queries are tested against real Python patterns, so Plan 02 builds materialization with confidence about what the parser delivers
- **Surface model is stable** — all downstream code (materialization, X-ray, annotations) consumes surface model types that are tested and complete

This is the risk-retirement increment. Every subsequent plan assumes tree-sitter works.

## Prerequisites

- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — already in `Directory.Packages.props` at version 1.3.0. Python grammar bundled with the package (same package as Ruby — `tree_sitter_python` language entry point)
- .NET 10 SDK (solution already targets this)

## North Star

Parse any Python file. Get structural elements back — classes, methods, functions, imports, decorators, type annotations, variables, docstrings, constants, type aliases. Never crash, never leak tree-sitter types, never block another thread. When the file is broken, get partial results and a diagnostic — never nothing.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Python` shall build targeting .NET 10
- The project shall reference `TreeSitter.DotNet`, `RepoQL.Contracts`, and `RepoQL.Indexing`
- The test project `RepoQL.Formats.Python.Tests` shall reference TUnit, AwesomeAssertions, and FakeItEasy
- Both projects shall be included in `RepoQL.sln`

### PythonConstants
- The constants class shall define node kinds: `py.type`, `py.member`, `py.function`
- The constants class shall define edge types: `HAS_PART`, `EXTENDS`, `IMPORTS`
- The constants class shall define property key constants matching the design's node property tables
- The constants class shall define annotation kinds: `python.metaprogramming`, `python.framework`

### PythonTreeSitterClient
- The client shall accept Python source code as a string and return a `PythonDocumentSurface`
- The client shall use `ThreadLocal<Parser>` so each thread gets its own parser instance
- The `Language` object shall be created once via `new Language("tree-sitter-python", "tree_sitter_python")` and shared across all threads
- No tree-sitter types (`TSNode`, `TSTree`, `TSParser`) shall appear in the client's public API
- When source is empty, the client shall return an empty surface (no exception)
- When source is null, the client shall throw `ArgumentNullException`

### Class Extraction
- The client shall extract class declarations with name, qualified name (with nesting), and byte range
- The client shall extract base classes from the `superclasses` argument list
- The client shall extract metaclass from keyword arguments (`metaclass=ABCMeta`)
- The client shall extract class decorators with name and arguments text
- The client shall extract class docstrings (first string literal in body)
- The client shall extract `__slots__` assignments
- The client shall extract class-level variable assignments with optional type annotations
- Nested classes shall have qualified names reflecting nesting: `Outer.Inner`

### Method and Function Extraction
- The client shall extract method definitions within classes with name, parameters, return type, and byte range
- The client shall extract top-level function definitions with name, parameters, return type, and byte range
- The client shall extract parameter details: name, type annotation, default value, kind (positional_only, positional_or_keyword, keyword_only, var_positional, var_keyword)
- The client shall extract method/function decorators with name and arguments text
- The client shall extract method/function docstrings
- The client shall detect async functions (`async def`)
- The client shall detect generators (body contains `yield` or `yield from`)
- The client shall detect async generators (async + yield)
- The client shall detect `async with` usage within async function bodies
- The client shall detect `async for` usage within async function bodies

### Instance Variable Extraction
- The client shall extract `self.x = ...` assignments from `__init__` method bodies
- When the assignment has an inline type annotation (`self.x: Type = ...`), the client shall capture the type
- When the target name matches a typed parameter, the client shall inherit the parameter's type
- The client shall NOT extract `self.x` assignments from methods other than `__init__`

### Import Extraction
- The client shall extract `import X` with module name
- The client shall extract `from X import a, b` with module and name list
- The client shall extract `from X import a as alias` with name and alias
- The client shall extract `from X import *` (star import)
- The client shall extract relative imports (`from . import X`, `from ..core import Y`) with relative level
- The client shall detect `if TYPE_CHECKING:` guards on import statements by checking the AST ancestor chain

### Constant and Type Alias Extraction
- The client shall extract module-level assignments as constants (name, type annotation, value text)
- The client shall detect `Final` annotations on constants
- The client shall detect ALL_CAPS naming convention on constants
- The client shall extract `type X = ...` (3.12+) type alias statements
- The client shall extract `X: TypeAlias = ...` type alias patterns
- The client shall extract `__all__` assignment values

### Visibility Detection
- The client shall determine visibility from naming conventions: `name` → public, `_name` → private, `__name` → private, `__name__` → public (dunder)
- Visibility detection shall be purely syntactic — inspect the name string

### Metaprogramming and Framework Hint Detection
- The client shall detect `__getattr__` definitions on classes
- The client shall detect `exec(...)` and `eval(...)` calls
- The client shall detect `type()` calls with 3 arguments (dynamic class creation)
- The client shall detect `setattr(...)` calls
- The client shall detect metaclass `__new__` and `__init_subclass__` definitions
- The client shall detect ORM field patterns: `models.*` calls, `db.Column` calls, `Field` calls at class level
- Hints shall carry: pattern name, byte range, extractable flag (for metaprogramming), rule_id and message (for framework)

### Extensible Query Execution
- The client shall expose a method to execute additional S-expression queries against a parsed tree and return matched captures with byte ranges
- This enables downstream plans to add extraction patterns without modifying the client's core query set

### Error Recovery
- When source contains syntax errors, the client shall return results for valid regions and skip `ERROR` nodes
- The client shall report the count of `ERROR` nodes encountered in `Stats.ErrorNodeCount`
- When the tree-sitter native library fails to load, the error message shall name the package and platform

### Thread Safety
- When 8 threads parse different Python files concurrently, all shall produce correct results
- When 8 threads parse the same Python source concurrently, all shall produce identical results
- No thread shall receive another thread's parser state

### Surface Model Types
- All parse results shall use plain C# records (no tree-sitter dependency)
- Each extracted element shall carry source byte range (start byte, end byte) for span creation
- Surface model types shall live in a `Surface/` subdirectory
- `PythonDocumentSurface` shall aggregate: Classes[], Functions[], Imports[], Constants[], TypeAliases[], AllExports, ModuleDocstring, MetaprogrammingHints[], FrameworkHints[], Stats
- `PythonClassInfo` shall carry: Name, QualifiedName, BaseClasses[], Metaclass, Decorators[], Methods[], ClassVariables[], InstanceVariables[], Slots, Docstring, ByteRange
- `PythonMethodInfo` shall carry: Name, IsAsync, IsGenerator, IsAsyncGenerator, UsesAsyncWith, UsesAsyncFor, Decorators[], Parameters[], ReturnType, Docstring, ByteRange
- `PythonFunctionInfo` shall carry: same as PythonMethodInfo (top-level functions have the same shape)
- `PythonParameterInfo` shall carry: Name, Type, Default, Kind (enum: PositionalOnly, PositionalOrKeyword, KeywordOnly, VarPositional, VarKeyword)
- `PythonDecoratorInfo` shall carry: Name (full dotted name), Arguments (raw text, nullable)
- `PythonImportInfo` shall carry: Module, Names[] (name, alias), IsRelative, RelativeLevel, IsStar, IsTypeCheckingOnly, ByteRange
- `PythonConstantInfo` shall carry: Name, TypeAnnotation, ValueText, IsFinal, IsAllCaps, ByteRange
- `PythonTypeAliasInfo` shall carry: Name, Definition, ByteRange
- `PythonVariableInfo` shall carry: Name, TypeAnnotation, VariableKind (Instance, Class), ByteRange
- `PythonMetaprogrammingHint` shall carry: PatternName, ByteRange, Extractable
- `PythonFrameworkHint` shall carry: Kind, RuleId, Message, ByteRange
- `PythonParseStats` shall carry: ClassCount, FunctionCount, ImportCount, LineCount, ErrorNodeCount
- `PythonByteRange` shall carry: StartByte, EndByte

## Constraints

- **Containment boundary** — all tree-sitter interop is in `PythonTreeSitterClient` and `PythonQueries`. No other class in the project may reference TreeSitter.DotNet types. This enables swapping parsers later without touching consumers
- **Query strings, not CST traversal** — use tree-sitter's S-expression query language for extraction. The design chose this over visitor pattern for robustness to grammar evolution
- **No materialization** — this plan validates the parser only. The client returns surface model types; conversion to graph nodes is Plan 02's scope
- **No encoding handling** — the client receives a string, not a file. PEP 263 encoding detection is a file I/O concern handled in Plan 02's `PythonLoader.LoadAsync`
- **Query accuracy caveat** — S-expression patterns in the design are based on documented grammar structure. Exact field names must be verified against the tree-sitter-python grammar during implementation. The Ruby format's query set was refined iteratively — expect the same here

## References

- [Python Format Design](../../designs/future/python-format.md) — Tree-Sitter Integration section, S-expression queries, Surface Model, thread safety approach
- [Python Parsing Research](../../research/python-parsing.md) — TreeSitter.DotNet evaluation, alternative parser comparison
- [Ruby Tree-Sitter Client](../../../src/Formats/RepoQL.Formats.Ruby/TreeSitter/RubyTreeSitterClient.cs) — reference implementation for thread-local parsers, query execution, surface model building
- [Ruby Queries](../../../src/Formats/RepoQL.Formats.Ruby/TreeSitter/RubyQueries.cs) — reference for S-expression query organization
- [Ruby Surface Model](../../../src/Formats/RepoQL.Formats.Ruby/Surface/) — reference for surface record patterns
- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — NuGet package (already in `Directory.Packages.props`)
- [tree-sitter-python grammar](https://github.com/tree-sitter/tree-sitter-python) — 570+ commits, official grammar
- [tree-sitter query syntax](https://tree-sitter.github.io/tree-sitter/using-parsers/queries) — S-expression pattern language
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — project structure conventions
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Parse errors produce partial results, never exceptions. The client catches all tree-sitter exceptions and translates them to diagnostics on the result object. Each extraction phase (classes, functions, imports, decorators, docstrings, variables, constants, type aliases) is independently try/caught — a malformed class never prevents function extraction.

Native library loading failure at startup is the one hard error — if the grammar can't load, there's nothing to recover. The error message must be actionable: name the NuGet package, the expected RID, and suggest `dotnet restore`.
