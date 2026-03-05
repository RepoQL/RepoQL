# Plan: Tree-sitter C# Parser

Replace Roslyn syntax-only parsing with tree-sitter in the C# format loader. Semantic analysis (MSBuildWorkspace) deferred to future out-of-process service. Unblocks NativeAOT.

## Why

Roslyn is incompatible with NativeAOT (reflection, dynamic assembly generation). Tree-sitter is a native library, already used for Go, Rust, Python, Ruby, PHP, C++. The C# grammar covers C# 1-13 with known gaps in collection expressions and interpolated raw strings — acceptable for structural extraction.

## Key Insight

`CSharpDocumentSurface`, `CSharpTypeInfo`, `CSharpMemberInfo`, `CSharpUsingInfo`, `CSharpNamespaceInfo` — the entire surface model is parser-agnostic. The tree-sitter client produces the same records. `Materialize`, schema views, templates, and all downstream code remain untouched.

## Scope

**In scope:**
- New `CSharpTreeSitterClient` (tree-sitter queries + surface model extraction)
- New `CSharpQueries` and `CSharpPatternGroup` (combined query, single-pass)
- Modify `CSharpLoader.LoadAsync` to use tree-sitter instead of Roslyn
- Tests for query correctness and surface model parity
- Remove `CSharpInventoryWalker` (Roslyn-specific)

**Out of scope (future work):**
- Removing Roslyn NuGet packages (still referenced by `CSharpWorkspaceHost`)
- Out-of-process semantic analysis service
- NativeAOT build configuration

---

## Steps

### 1. Create `CSharpQueries` and `CSharpPatternGroup`

**File:** `src/formats/repoql.formats.dotnet/treesitter/CSharpQueries.cs`
**File:** `src/formats/repoql.formats.dotnet/treesitter/CSharpPatternGroup.cs`

Follow the Go/PHP pattern: one `const string` per construct, concatenated into `CombinedQuery` for single-pass extraction. `ClassifyPattern(int patternIndex)` dispatches matches.

**Query groups needed** (mapped from `CSharpInventoryWalker` Visit* methods):

| Group | tree-sitter node type(s) | Captures |
|-------|--------------------------|----------|
| UsingDirectives | `using_directive` | name, alias, static modifier |
| NamespaceDeclarations | `namespace_declaration`, `file_scoped_namespace_declaration` | name |
| ClassDeclarations | `class_declaration` | name, base_list, type_parameters, modifiers |
| StructDeclarations | `struct_declaration` | name, base_list, type_parameters, modifiers |
| RecordDeclarations | `record_declaration`, `record_struct_declaration` | name, base_list, parameter_list, modifiers |
| InterfaceDeclarations | `interface_declaration` | name, base_list, type_parameters, modifiers |
| EnumDeclarations | `enum_declaration` | name, base_list, modifiers |
| MethodDeclarations | `method_declaration` | name, return_type, parameter_list, modifiers |
| ConstructorDeclarations | `constructor_declaration` | name, parameter_list, modifiers |
| PropertyDeclarations | `property_declaration` | name, type, modifiers |
| FieldDeclarations | `field_declaration` | type, variable_declarator(s), modifiers |
| EventDeclarations | `event_declaration`, `event_field_declaration` | name, type, modifiers |
| IndexerDeclarations | `indexer_declaration` | type, parameter_list, modifiers |
| Comments | `comment` | text (for doc comments) |

**Validate** queries against the [tree-sitter-c-sharp grammar](https://github.com/tree-sitter/tree-sitter-c-sharp) node types before writing. Use `repoql explore` or `ast-grep` to verify.

### 2. Create `CSharpTreeSitterClient`

**File:** `src/formats/repoql.formats.dotnet/treesitter/CSharpTreeSitterClient.cs`

Follow the `GoTreeSitterClient` / `PhpTreeSitterClient` pattern:

```
class CSharpTreeSitterClient : IDisposable
    static Language SharedLanguage
    static Query SharedCombinedQuery
    ThreadLocal<Parser> _parsers

    CSharpDocumentSurface Parse(string sourceCode)
    -> ExecuteCombinedQuery(root)
    -> dispatch into ExtractNamespaces, ExtractTypes, ExtractMembers, ExtractUsings
    -> return populated CSharpDocumentSurface
```

**Critical mappings:**
- Tree-sitter byte ranges → `DocumentSpan` (using `TextLineMap` for line/char conversion)
- Modifier extraction from `modifier` child nodes (public/private/static/async/etc.)
- Doc comment extraction from leading `comment` trivia
- Base type / interface list extraction from `base_list` nodes
- Parameter extraction from `parameter_list` → `CSharpParameterInfo`
- Namespace nesting tracking (stack-based, like the walker)
- Type nesting tracking (stack-based, like the walker)
- Deterministic ID generation via `CSharpIdFactory` (needs byte offset → TextSpan conversion)

**Thread safety:** `ThreadLocal<Parser>` for parser instances, shared `Language` and `Query` (both thread-safe after creation).

### 3. Modify `CSharpLoader.LoadAsync`

**File:** `src/Formats/RepoQL.Formats.DotNet/CSharpLoader.cs`

Replace:
```csharp
// Current Roslyn path
var syntaxTree = CSharpSyntaxTree.ParseText(text, ParseOptions, ...);
var root = await syntaxTree.GetRootAsync(cancellationToken);
var walker = new CSharpInventoryWalker(documentId, lineMap);
walker.Visit(root);
// ... build surface from walker ...
```

With:
```csharp
// New tree-sitter path
var surface = _treeSitterClient.Parse(text);
// surface is already a populated CSharpDocumentSurface
```

**Changes:**
- Add `CSharpTreeSitterClient _treeSitterClient` field (lazy-initialized, like `GoLoader._client`)
- Replace parse + walk + surface construction with single `_treeSitterClient.Parse(text)` call
- Remove `AnnotateSemanticInfoAsync` call (semantic analysis deferred)
- `CSharpDocumentState.References` → empty (no cross-file refs without semantic model)
- `CSharpDocumentState.Diagnostics` → empty (no compiler diagnostics without Roslyn)
- `CSharpDocumentState.GeneratedDocuments` → empty (no source generators without Roslyn)
- Remove `DefaultReferences` and `CreateDefaultReferences` (Roslyn compilation artifacts)
- Keep `CSharpWorkspaceHost` reference but don't call it

**The `DocumentModel` constructor** currently receives `syntaxTree` as the model object. Change to pass the surface model instead (like Go passes `surface`).

### 4. Remove `CSharpInventoryWalker`

**File:** Delete `src/Formats/RepoQL.Formats.DotNet/CSharpInventoryWalker.cs`

The walker is purely Roslyn-specific (`CSharpSyntaxWalker` subclass). All its extraction logic moves into `CSharpTreeSitterClient`.

### 5. Implement `IDisposable` on `CSharpLoader`

Add disposal of `CSharpTreeSitterClient` (follows Go pattern where `GoLoader` implements `IDisposable` and disposes the tree-sitter client).

### 6. Tests

**File:** `src/tests/RepoQL.Tests/Formats/CSharpTreeSitterClientTests.cs` (or similar)

**Parity tests** — parse the same C# files with both the old Roslyn walker and the new tree-sitter client, compare surface models:
- Namespace extraction (block-scoped and file-scoped)
- Type declarations (class, struct, record, interface, enum)
- Member declarations (method, property, field, event, constructor, indexer)
- Using directives (regular, static, aliased)
- Modifiers (public/private/protected/internal, static, async, virtual, override, sealed, abstract, partial)
- Base types and interfaces
- Parameter lists
- Doc comment extraction
- Nested types
- Nested namespaces

**Edge case tests:**
- Empty file
- File with only using directives
- Partial classes
- Primary constructors (records)
- Generic types and methods
- Attributes (verify they don't break parsing — extraction is optional)
- Files with syntax errors (verify error tolerance)
- Modern C# features: top-level statements, global usings, file-scoped namespaces, raw string literals

**Regression test:** Parse `src/Formats/RepoQL.Formats.DotNet/*.cs` (the loader's own source files) and verify the surface model contains expected types and members.

---

## What Stays Unchanged

- `CSharpDocumentSurface`, `CSharpDocumentState`, `CSharpTypeInfo`, `CSharpMemberInfo`, all surface model types
- `CSharpLoader.Materialize` — consumes the surface model, parser-agnostic
- `csharp_views.sql` — queries node properties, parser-agnostic
- `csharp_enums.sql` — enum type definitions
- Liquid templates — consume surface model
- `CSharpIdFactory` — deterministic ID generation (may need minor adaptation for byte offsets)
- `CSharpParser` (pipeline stage) — calls `CSharpLoader`, doesn't know about parser internals
- `CSharpAnalyzer` — consumes diagnostics from state, just gets empty list for now
- `CSharpWorkspaceHost` — stays in codebase, unused until out-of-process work

## What Gets Removed

- `CSharpInventoryWalker` — replaced by `CSharpTreeSitterClient`
- Roslyn `ParseOptions`, `DefaultReferences`, `CreateDefaultReferences` in `CSharpLoader`
- `AnnotateSemanticInfoAsync` call path (deferred)
- `CSharpSemanticUtilities` — only used for `BuildSymbolKey` during semantic analysis

## Risks

| Risk | Mitigation |
|------|------------|
| Tree-sitter C# grammar gaps (collection expressions, interpolated raw strings) | Error tolerance produces partial tree; structure extraction still works for surrounding code. Track grammar updates. |
| Doc comment extraction differences | Tree-sitter sees comments as plain text, not structured XML trivia. Extract `///` prefix and parse XML manually if needed, or extract raw text. |
| Modifier extraction complexity | C# has many modifiers; tree-sitter represents them as child nodes. Build a helper to extract from `modifier` nodes. |
| ID determinism | `CSharpIdFactory` uses `TextSpan` (char offset, length). Tree-sitter gives byte offsets. For UTF-8 ASCII source (99% of C#), these are identical. For non-ASCII, need byte→char conversion. |
| `SymbolKey` loss | Already nullable in the surface model. Only populated during semantic analysis. Tree-sitter path leaves it null. |

## Order of Work

1. Write and validate tree-sitter queries (most effort, most risk)
2. Build `CSharpTreeSitterClient` with Parse method
3. Write parity tests against known C# files
4. Modify `CSharpLoader.LoadAsync` to use tree-sitter
5. Run full test suite, fix regressions
6. Remove `CSharpInventoryWalker`
