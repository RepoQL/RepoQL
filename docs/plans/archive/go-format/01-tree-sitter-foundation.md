---
description: Plan for Go format — project scaffolding, TreeSitter.DotNet integration, query-based extraction, and thread-safety validation
tags: [format, go, golang, tree-sitter, plan, parser]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Go — Tree-Sitter Foundation

Implements: [Go Format Design](../../designs/future/go-format.md) — Tree-Sitter Integration, Surface Model, Project Structure

## Scope

**Covers:**
- New project `RepoQL.Formats.Go` with TreeSitter.DotNet dependency
- New test project `RepoQL.Formats.Go.Tests`
- `GoTreeSitterClient` — thread-safe wrapper containing all tree-sitter interop
- `GoQueries` — S-expression query strings for structural extraction
- Surface model types for parse results (no tree-sitter types escape)
- Thread-safety validation via concurrent parsing tests
- Error recovery validation
- Solution file updates

**Does not cover:**
- Classification or media types (Plan: 02-core-format-loader)
- Materialization to graph nodes/edges (Plan: 02-core-format-loader)
- DI registration or pipeline integration (Plan: 02-core-format-loader)
- SQL views (Plans: 02 through 05)
- Type definitions, aliases, constants, variables, directives (Plan: 03-extended-structure)
- go.mod / go.work parsing (Plan: 04-module-metadata)

## Enables

Once this exists:
- **Risk is retired** — TreeSitter.DotNet loads the Go grammar, parses real files, handles errors, and is thread-safe. The riskiest technical choice is validated before downstream code is written
- **Plan 02 can proceed** — the core format loader consumes `GoTreeSitterClient` directly
- **Query coverage is known** — S-expression queries are tested against Go patterns, so Plan 02 can build materialization with confidence about what the parser delivers

This is the risk-retirement increment. Every subsequent plan assumes tree-sitter works for Go.

## Prerequisites

- TreeSitter.DotNet already in `Directory.Packages.props` (added for Ruby format)
- .NET 10 SDK (solution already targets this)

## North Star

Parse any Go file. Get structural elements back. Never crash, never leak tree-sitter types, never block another thread. When the file is broken, get partial results and a diagnostic — never nothing.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Go` shall build targeting .NET 10
- The project shall reference `TreeSitter.DotNet`, `RepoQL.Contracts`, and `RepoQL.Indexing`
- The test project `RepoQL.Formats.Go.Tests` shall reference TUnit, AwesomeAssertions, and FakeItEasy
- Both projects shall be included in `RepoQL.sln`

### GoTreeSitterClient
- The client shall accept Go source code as a string and return a `GoDocumentSurface`
- The client shall use `ThreadLocal<Parser>` so each thread gets its own parser instance
- The `Language` object shall be created once and shared across all threads
- No tree-sitter types (`TSNode`, `TSTree`, `TSParser`) shall appear in the client's public API
- When source is empty, the client shall return an empty result (no exception)
- When source is null, the client shall throw `ArgumentNullException`

### Query-Based Extraction — Package and Imports
- The client shall extract the package name from the `package_clause`
- The client shall extract import specs from both single and grouped `import_declaration`
- Each import shall carry: path (unquoted string), alias (if present), span (byte range)
- Blank imports (`_`) shall be extracted with alias `"_"`
- Dot imports (`.`) shall be extracted with alias `"."`
- Named alias imports shall carry the alias name
- Default imports (no alias) shall carry null alias

### Query-Based Extraction — Structs and Fields
- The client shall extract struct declarations with name and byte range
- The client shall extract fields within each struct with: name, type (as string), byte range
- The client shall extract struct tags (raw string, e.g., `` `json:"name" db:"col"` ``)
- The client shall detect embedded fields (anonymous fields without explicit name) and mark them with `is_embedded: true`
- Embedded fields shall use the type name as the field name (e.g., `sync.Mutex` → name `"Mutex"`)

### Query-Based Extraction — Interfaces
- The client shall extract interface declarations with name and byte range
- The client shall extract method specs within each interface with: name, parameter text, return type text, byte range
- The client shall detect embedded interfaces within interface declarations (type names without method signatures)

### Query-Based Extraction — Functions
- The client shall extract top-level function declarations (no receiver) with: name, parameter text, return type text, byte range
- Parameter text shall be the raw text of the parameter list

### Query-Based Extraction — Methods
- The client shall extract method declarations (with receiver) with: name, receiver text, parameter text, return type text, byte range
- The client shall extract the receiver type name (stripping pointer `*` prefix if present)
- The client shall detect whether the receiver is a pointer receiver (`*T`) or value receiver (`T`)
- The receiver variable name shall be extracted (e.g., `s` in `func (s *Server) Serve()`)

### Import Classification
- The client shall classify imports by path pattern: no dots → `stdlib`, otherwise `external`
- Internal classification (requires go.mod context) is deferred to Plan 02 or multi-file analysis

### Visibility
- The client shall determine exported/unexported status for all named elements using `char.IsUpper(name[0])`
- Structs, interfaces, functions, methods, and fields shall each carry an `is_exported` flag

### Error Recovery
- When source contains syntax errors, the client shall return results for valid regions and skip `ERROR` nodes
- The client shall report the count of `ERROR` nodes in `GoDocumentSurface.ErrorNodeCount`
- When the tree-sitter native library fails to load, the error message shall name the package and platform

### Thread Safety
- When 8 threads parse different Go files concurrently, all shall produce correct results
- When 8 threads parse the same Go source concurrently, all shall produce identical results
- No thread shall receive another thread's parser state

### Surface Model Types
- Parse results shall use plain C# records (no tree-sitter dependency)
- `GoDocumentSurface` shall carry: PackageName, Imports[], Structs[], Interfaces[], Functions[], Methods[], Stats, ErrorNodeCount
- `GoStructInfo` shall carry: Name, IsExported, Fields[], ByteRange
- `GoFieldInfo` shall carry: Name, TypeName, Tag, IsEmbedded, IsExported, ByteRange
- `GoInterfaceInfo` shall carry: Name, IsExported, Methods[], EmbeddedInterfaces[], ByteRange
- `GoInterfaceMethodInfo` shall carry: Name, Parameters (text), ReturnType (text), ByteRange
- `GoFunctionInfo` shall carry: Name, IsExported, Parameters (text), ReturnType (text), ByteRange
- `GoMethodInfo` shall carry: Name, IsExported, ReceiverName, ReceiverType, IsPointerReceiver, Parameters (text), ReturnType (text), ByteRange
- `GoImportInfo` shall carry: Path, Alias, Category (stdlib/external), ByteRange
- `GoParseStats` shall carry: StructCount, InterfaceCount, FunctionCount, MethodCount, ImportCount, LineCount
- `GoByteRange` shall carry: StartByte, EndByte
- All surface model types shall live in a `Surface/` subdirectory

### Extensible Query Execution
- The client shall expose a method to execute additional S-expression queries against a parsed tree and return matched captures with byte ranges
- This enables Plan 03 to add extraction patterns for constants, type definitions, directives, etc. without modifying the client's core query set

### Test Fixtures
- `Fixtures/simple_struct.go` — struct with exported/unexported fields, tags, methods (value and pointer receiver)
- `Fixtures/interfaces.go` — interface declarations with methods and embedded interfaces
- `Fixtures/functions.go` — top-level functions, exported and unexported
- `Fixtures/imports.go` — single import, grouped imports, blank, dot, and aliased imports
- `Fixtures/embedding.go` — struct with embedded fields (same-package and qualified types)
- `Fixtures/malformed.go` — syntax errors for error tolerance validation

## Constraints

- **Containment boundary** — all tree-sitter interop is in `GoTreeSitterClient` and `GoQueries`. No other class in the project may reference TreeSitter.DotNet types. This enables swapping parsers later without touching consumers
- **Query strings, not CST traversal** — use tree-sitter's S-expression query language for extraction. The design chose this over visitor pattern for robustness to grammar evolution
- **No materialization** — this plan validates the parser only. The client returns surface model types; conversion to graph nodes is Plan 02's scope
- **Parameters and return types as text** — Go's type syntax is rich (channels, maps, function types, generics). Parameters and return types are captured as raw text strings, not parsed into structured representations. Structured parameter parsing is an extension point

## References

- [Go Format Design](../../designs/future/go-format.md) — Tree-Sitter Integration section, S-expression queries, surface model, visibility
- [Go Parsing Research](../../research/go-parsing-from-dotnet.md) — TreeSitter.DotNet evaluation, platform coverage, risk assessment
- [Ruby Tree-Sitter Client](../../../src/Formats/RepoQL.Formats.Ruby/TreeSitter/RubyTreeSitterClient.cs) — proven pattern for thread-safe tree-sitter wrapper
- [Ruby Queries](../../../src/Formats/RepoQL.Formats.Ruby/TreeSitter/RubyQueries.cs) — S-expression query pattern
- [Ruby Surface Model](../../../src/Formats/RepoQL.Formats.Ruby/Surface/) — record-based surface model pattern
- [TreeSitter.DotNet](https://www.nuget.org/packages/TreeSitter.DotNet) — NuGet package with bundled Go grammar
- [tree-sitter query syntax](https://tree-sitter.github.io/tree-sitter/using-parsers/queries) — S-expression pattern language
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — project structure conventions
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Parse errors produce partial results, never exceptions. The client catches all tree-sitter exceptions and translates them to diagnostics on the result object.

Native library loading failure at startup is the one hard error — if the grammar can't load, there's nothing to recover. The error message must be actionable: name the NuGet package, the expected RID, and suggest `dotnet restore`.
