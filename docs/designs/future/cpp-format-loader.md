# C/C++ Format Loader — Design

## North Star

An agent queries C/C++ structure — classes, namespaces, templates, includes — without reading source files, without a build system, and without running the preprocessor. Where the preprocessor hides structure, the agent knows exactly where it can't see.

See `docs/north-star/formats/cpp.md` for the full declaration set.

## Context

RepoQL needs a format loader for C and C++ source files. This loader participates in the existing indexing pipeline (classification → parsing → analysis → commit) and produces Records fitting the frozen 5-table schema (artifact, node, edge, span, annotation).

**Informed by:**
- `docs/research/cpp-parsing-options.md` — parser evaluation (tree-sitter, libclang, clangd, ctags, srcML, ANTLR4)
- `docs/flows/future/cpp-indexing.md` — how C/C++ files flow through the pipeline

**Key research findings:**
- Tree-sitter: fast, zero-config, no dependencies, but no preprocessor expansion. Known macro interference on Qt, Windows DLL, gtest, namespace macros.
- libclang: full semantic analysis but requires headers, ~37 MB native binary per platform, silently incomplete without KeepGoing flag. ClangSharp provides .NET bindings.
- TreeSitter.DotNet does NOT bundle C++ grammar. The C grammar is 4 years stale. Grammar must be built and bundled separately.
- ANTLR4: 71% parse success rate. Eliminated.
- Universal Ctags and srcML: GPL. Eliminated.

## Constraints

| Constraint | Source | Impact |
|------------|--------|--------|
| Runs on a developer laptop | CLAUDE.md | No multi-GB memory requirements. Rules out clangd as primary. |
| One bad file never breaks anything else | CLAUDE.md | Parse failures must be isolated. ERROR nodes must not cascade. |
| Time-to-usable is primary KPI | CLAUDE.md | Zero-config path must exist. No mandatory build system setup. |
| Budget is contract | CLAUDE.md | X-ray summaries must be token-efficient. Structure, not content. |
| Schema frozen: 5 tables | CLAUDE.md | Extend via views/macros/UDFs. No new tables. |
| Errors never cascade | CLAUDE.md | Format code hardened against hangs, crashes, unexpected failures. |
| Results trustworthy or loudly not | CLAUDE.md | Macro-hidden structure must be explicitly annotated, not silently omitted. |
| Docs with features | CLAUDE.md | `help://` documentation for C/C++ format ships with the loader. |

---

## North-Star Coverage

How this design covers the declarations in `docs/north-star/formats/cpp.md`. **Covered** = Option A (tree-sitter only). **Partial** = covered with limitations. **Requires Option B** = needs libclang enrichment.

| North-Star Section | Coverage | Notes |
|--------------------|----------|-------|
| Discovery | Covered | Headlines from x-ray, media type classification, C vs C++ distinction |
| Header/Source Split | Covered | Multi-file analysis links by qualified name + arity. Complete type view via SQL join. |
| Preprocessor Boundary | Covered | `#include` and `#define` as graph nodes. `#ifdef`/`#if` as annotations (block boundaries, not graph nodes). Macro invocation heuristics. Known macro family detection. |
| Classes, Structs, Unions | Covered | Full extraction: members, access specifiers, virtual/override/final, bitfields, friends. |
| Inheritance & Polymorphism | Partial | Base classes by name. Cross-file `EXTENDS` edges via multi-file analysis. Override chain tracing works for qualified-name matches. Precise overload resolution requires Option B. |
| Templates | Covered | Parameters, specializations, constraints as text. Variadic, fold expressions, extern template. Instantiation explicitly out of scope per north star. |
| Namespaces | Covered | Properties on nodes, SQL view unification. Anonymous, inline, nested all captured. |
| Enums | Covered | Scoped vs unscoped, underlying type, enumerator values. |
| Functions | Covered | Signatures, qualifiers, function pointers, variadic functions. Lambda capture lists partial — tree-sitter may not structure all captures. |
| Type Aliases, Constants, Storage | Covered | typedef, using, constexpr, thread_local, volatile, static locals. |
| Include Graph | Covered | Direct edges in hot path, transitive in multi-file. System headers marked unresolved. |
| C++20 Concepts | Covered | Concept definitions, requires clauses as text. |
| C++20 Modules | Covered | Parse without crashing, index declarations. Narrow scope per north star. |
| Error Handling | Covered | noexcept detection, try/catch structure, throw types. |
| Coroutines | Covered | co_await/co_yield/co_return detection, return types. |
| Attributes | Covered | `[[nodiscard]]`, `[[deprecated]]`, vendor attributes where syntactically visible. |
| Documentation Comments | Covered | Doxygen `/** */` and `///` extraction with @param/@returns tags. |
| Memory/Resource | Covered | Destructors, smart pointer types, Rule of Five, `= delete`. |
| Testing | Covered | Test macro pattern recognition with honest annotation of macro limitations. |
| Build Configuration | Partial | File classification only. Deeper build-system analysis is not in scope for the format loader. |
| Integrity | Covered | Error isolation, parse error classification, macro annotation, one-file-never-breaks-anything. |

**What Option B adds:** Precise type resolution (overload disambiguation in header/source linking), macro expansion (seeing generated members), cross-TU symbol resolution, template instantiation analysis. These fill the "partial" gaps and sharpen accuracy — but Option A delivers the vast majority of the north-star with zero configuration.

---

## Design

### Approach: Tree-sitter Primary, libclang Optional

Two approaches were evaluated:

**Option A: Tree-sitter only** — Use tree-sitter-cpp for all C/C++ parsing (C++ grammar handles C files correctly). Accept the preprocessor boundary. Annotate where macros hide structure.

**Option B: Hybrid (tree-sitter + libclang)** — Tree-sitter as the fast zero-config default. When `compile_commands.json` is detected, optionally enrich with libclang type resolution, macro expansion, and cross-TU symbol matching.

**Decision: Build Option A first. Design for Option B.**

Rationale:
- Option A satisfies 90%+ of the north-star declarations with zero configuration.
- The remaining declarations (type resolution, macro expansion, template instantiation) require Option B, which requires headers and build system information that may not exist.
- Building A first with clean interfaces means B can be added incrementally without rearchitecting.
- The research's validation experiments (1-3) should be completed before committing to B's complexity.

The critical interface is `ICppEnricher` — an optional post-parse step that can inject additional edges and annotations into Records produced by tree-sitter. libclang is one possible enricher; others (compilation database parsing, heuristic type inference) could follow the same interface.

### Project Structure

```
src/Formats/RepoQL.Formats.Cpp/
├── RepoQL.Formats.Cpp.csproj
├── Classification/
│   └── CppClassifier.cs              # Media type refinement
├── Parsing/
│   ├── CppParser.cs                  # Pipeline processor (entry point)
│   ├── CppTreeSitterClient.cs        # Tree-sitter grammar management
│   ├── CppMaterializer.cs            # CST → Records transformation
│   └── CppXRayGenerator.cs           # Headline, summary, structure
├── Analysis/
│   ├── CppSingleFileAnalyzer.cs      # Include edges, doc comments, attributes
│   ├── CppMultiFileAnalyzer.cs       # Header/source linking, inheritance graph
│   └── MacroInterferenceDetector.cs  # ERROR node classification
├── Schema/
│   └── cpp_views.sql                 # SQL views for C/C++ querying
├── Enrichment/
│   └── ICppEnricher.cs               # Interface for optional enrichment (future)
└── docs/
    └── cpp-format.md                 # help:// documentation
```

### Edge Types and Node Kinds

Following Ruby's pattern of defining format-specific constants:

```csharp
internal static class CppNodeKinds
{
    public const string Document = "document";
    public const string Type = "cpp.type";       // class, struct, union, enum, concept
    public const string Member = "cpp.member";    // methods, constructors, fields
    public const string Function = "cpp.function"; // free functions
    public const string Namespace = "cpp.namespace";
    public const string Include = "cpp.include";
    public const string Macro = "cpp.macro";
    public const string Using = "cpp.using";
    public const string Module = "cpp.module";    // C++20
}

internal static class CppEdgeTypes
{
    public const string HasPart = "HAS_PART";
    public const string RefersTo = "REFERS_TO";   // includes, forward declarations, using declarations
    public const string Extends = "EXTENDS";       // class inheritance (properties: access, is_virtual)
    // Friend declarations use REFERS_TO with relationship=friend in properties
}
```

**Cross-language visibility:** `cpp.type` nodes appear in the shared `Types` view (`WHERE kind LIKE '%.type'`). `cpp.member` and `cpp.function` must be added to the shared `Functions` view's kind filter (currently hardcoded in `functions.sql`).

### Grammar Management

**Problem:** TreeSitter.DotNet does not bundle C++ grammar. The pinned C grammar is 4 years stale.

**Solution:** Build tree-sitter-cpp from its official repository and bundle the compiled native library alongside the format project. Follow the same pattern as TreeSitter.DotNet's runtime-specific native library loading, but with our own grammar build. (tree-sitter-cpp handles both C and C++ files.)

```
runtimes/
├── win-x64/native/tree-sitter-cpp.dll
├── linux-x64/native/libtree-sitter-cpp.so
└── osx-arm64/native/libtree-sitter-cpp.dylib
```

**Build from master:** The tree-sitter-cpp master branch (post February 2025) includes C++20 module syntax. The latest release (v0.23.4, November 2024) does not. Build from a pinned commit on master to get module support.

**Grammar versioning:** Pin to specific commit SHAs. Record these in the `.csproj` as metadata. Grammar updates are explicit, tested, never automatic.

**Thread safety:** `ThreadLocal<Parser>` per grammar, matching the Ruby pattern in `RubyTreeSitterClient`. Tree-sitter parsers are not thread-safe but the trees they produce are read-only.

### Classification

Simple extension-based classification with content sniffing for `.h` ambiguity. See the flow document for the full decision table.

The classifier is a single `IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>` processor. It checks `ProvisionalMediaType` first (computed from file extension by the file system layer), then applies content sniffing only for `.h` files.

**Extension handling:**
- `.cpp`, `.cc`, `.cxx` → `code.cpp` (C++ source)
- `.hpp`, `.hh`, `.hxx` → `code.cpp-header` (C++ header)
- `.ipp`, `.tpp`, `.inl` → `code.cpp-inline` (new — C++ inline/template implementation)
- `.c` → `code.c` (C source)
- `.h` → ambiguous, see below

**`.h` ambiguity resolution:**
1. Check if a sibling `.cpp`/`.cc`/`.cxx` exists with the same stem → C++ header
2. Scan first ~100 lines for C++ keywords → C++ header
3. Default → C header

This is a heuristic. It may misclassify some files. The consequence of misclassification is minor: tree-sitter-cpp extends tree-sitter-c, so parsing a C file with the C++ grammar produces correct results. Parsing a C++ file with the C grammar produces partial results (classes/namespaces may not parse). The content sniffer catches the important cases.

### Materialization

`CppMaterializer` performs a single depth-first walk of the CST and emits Records incrementally. It does NOT build an intermediate AST — it transforms directly from tree-sitter nodes to Records.

**State tracking during walk:**
- Current namespace stack (for qualified names)
- Current class/struct stack (for member context)
- Current access specifier (public/private/protected)
- Current template parameters (for wrapping declarations)

**Node mapping:** See the flow document's extraction table for the full CST-to-graph mapping.

**Qualified names:** Symbols are named with their full qualification: `net::ConnectionPool::connect`. This enables cross-file matching during multi-file analysis — a definition `net::ConnectionPool::connect(...)` in `pool.cpp` matches the declaration in `pool.h`.

### Macro Interference Detection

The `MacroInterferenceDetector` classifies ERROR and MISSING nodes in the CST by examining context. This is heuristic — it cannot be certain a macro caused the error — but it provides the honest annotation the north-star demands.

**Detection patterns:**

| Context | Heuristic | Confidence |
|---------|-----------|------------|
| ERROR node after ALL_CAPS identifier inside class body | Framework member-injection macro | High |
| ERROR node after ALL_CAPS identifier before class declaration | Export/visibility macro | High |
| MISSING `#endif` near `extern "C"` | Preprocessor boundary pattern | High |
| ERROR node after identifier matching known patterns (`Q_OBJECT`, `TEST_F`, `EXPORT_API`, etc.) | Known macro family | Very high |
| ERROR node in template `<>` context | Template complexity (not macro) | Medium |
| Other ERROR node | Unknown cause | Low |

**Known macro families:** Ship with a default list of known structural macros (Qt, Windows SDK, Google Test, Catch2, Boost). This list is a heuristic aid, not a filter — unknown macros are still detected by the ALL_CAPS pattern.

**Annotation format:**
```
kind: "lint"
severity: "info"
rule_id: "cpp/macro_interference"
message: "Macro invocation 'Q_OBJECT' may hide class members — structure after this point may be incomplete"
```

### Multi-File Analysis

Runs during idle processing (after hot path drains for the epoch). This is where the header/source split gets resolved.

**Header/source linking:**
- For each function definition node with a qualified name (e.g., `ConnectionPool::connect`), search for a matching declaration node in the index.
- Create `REFERS_TO` edges with `relationship=defines` in properties between declaration and definition nodes.
- Match by qualified name + arity. Signature comparison is not attempted (would require type resolution). Arity disambiguates the most common overload cases.

**Include graph (transitive completion):**
- Direct include edges (`REFERS_TO` from `#include` node to target document) are created during single-file analysis.
- Multi-file analysis computes **transitive** include chains — if A includes B and B includes C, create an edge recording that A transitively depends on C.
- System includes (`<>`) are recorded but marked unresolved when the target header isn't in the index.

**Inheritance graph:**
- During hot-path parsing, each class records its base classes by name in properties.
- During multi-file analysis, base class names are resolved to their definition nodes across the index.
- Create `EXTENDS` edges (following Ruby's pattern) with `access` property (`public`, `private`, `protected`) and `is_virtual` property.

**Namespace unification:**
- No new edges needed — namespaces are recorded as properties on nodes. The `cpp_namespace_members` view handles unification via SQL.

### SQL Views

C/C++ query patterns expressed as views over the frozen schema. These follow the conventions established by `types.sql` and `functions.sql` — extracting names from `properties` JSON, deriving file URIs via `repository_uri_container()`, and using node `kind` with the `cpp.` prefix.

**Cross-language visibility:** `cpp.type` nodes automatically appear in the shared `Types` view (`WHERE kind LIKE '%.type'`). The shared `Functions` view's kind filter must be updated to include `'cpp.member'` and `'cpp.function'` (see `functions.sql`).

```sql
-- cpp_classes: All class/struct/union declarations (C++ specific projection over Types)
CREATE OR REPLACE VIEW cpp_classes AS
SELECT
    n.uri,
    repository_uri_container(n.uri) AS file_uri,
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'kind' AS type_kind,  -- 'class', 'struct', 'union'
    n.properties->>'accessibility' AS default_access,
    n.properties->>'extends' AS extends,
    n.properties->>'is_abstract' AS is_abstract,
    TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER) AS start_line,
    TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER) AS end_line,
    n.headline,
    n.id AS node_id, n.span_id
FROM node n
WHERE n.kind = 'cpp.type'
  AND n.properties->>'kind' IN ('class', 'struct', 'union');

-- cpp_functions: All function declarations and definitions (C++ specific projection over Functions)
CREATE OR REPLACE VIEW cpp_functions AS
SELECT
    n.uri,
    repository_uri_container(n.uri) AS file_uri,
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'declaring_type' AS declaring_type,
    n.properties->>'return_type' AS return_type,
    n.properties->>'accessibility' AS access,
    COALESCE(n.properties->>'signature', n.headline) AS signature,
    COALESCE(n.properties->>'is_virtual', 'false') = 'true' AS is_virtual,
    COALESCE(n.properties->>'is_pure_virtual', 'false') = 'true' AS is_pure_virtual,
    COALESCE(n.properties->>'is_noexcept', 'false') = 'true' AS is_noexcept,
    COALESCE(n.properties->>'is_constexpr', 'false') = 'true' AS is_constexpr,
    COALESCE(n.properties->>'is_static', 'false') = 'true' AS is_static,
    TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER) AS start_line,
    TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER) AS end_line,
    n.headline,
    n.id AS node_id, n.span_id
FROM node n
WHERE n.kind IN ('cpp.member', 'cpp.function')
  AND n.properties->>'kind' IN ('method', 'constructor', 'function');

-- cpp_includes: Include graph
CREATE OR REPLACE VIEW cpp_includes AS
SELECT
    n.properties->>'target' AS target_header,
    n.properties->>'style' AS include_style,  -- '<>' or '""'
    repository_uri_container(n.uri) AS source_uri,
    n.id AS node_id
FROM node n
WHERE n.kind = 'cpp.include';

-- cpp_templates: Template declarations and specializations
CREATE OR REPLACE VIEW cpp_templates AS
SELECT
    n.uri,
    n.properties->>'name' AS name,
    n.properties->>'template_params' AS template_params,
    n.properties->>'base_template' AS base_template,
    n.properties->>'specialization_args' AS template_args,
    CASE WHEN n.properties->>'base_template' IS NOT NULL THEN 'specialization' ELSE 'primary' END AS template_kind,
    repository_uri_container(n.uri) AS file_uri,
    n.id AS node_id
FROM node n
WHERE n.properties->>'is_template' = 'true'
  AND n.kind LIKE 'cpp.%';

-- cpp_enums: Enum declarations
CREATE OR REPLACE VIEW cpp_enums AS
SELECT
    n.uri,
    n.properties->>'name' AS name,
    n.properties->>'is_scoped' AS is_scoped,
    n.properties->>'underlying_type' AS underlying_type,
    repository_uri_container(n.uri) AS file_uri,
    n.id AS node_id
FROM node n
WHERE n.kind = 'cpp.type'
  AND n.properties->>'kind' = 'enum';

-- cpp_macro_invocations: Known macro call sites (from annotations)
CREATE OR REPLACE VIEW cpp_macro_invocations AS
SELECT
    an.id,
    an.message,
    json_extract_string(an.data, '$.macro_name') AS name,
    json_extract_string(an.data, '$.context') AS context,
    repository_uri_container(doc.uri) AS file_uri,
    TRY_CAST(json_extract_string(an.data, '$.start_line') AS INTEGER) AS start_line,
    TRY_CAST(json_extract_string(an.data, '$.end_line') AS INTEGER) AS end_line,
    an.target_span_id AS span_id
FROM annotation an
JOIN node doc ON doc.id = an.scope_document_id
WHERE an.rule_id = 'cpp/macro_interference';

-- cpp_namespace_members: Unified namespace view across all files
CREATE OR REPLACE VIEW cpp_namespace_members AS
SELECT
    n.properties->>'namespace' AS namespace,
    n.properties->>'name' AS name,
    n.properties->>'kind' AS member_kind,
    n.properties->>'accessibility' AS accessibility,
    repository_uri_container(n.uri) AS file_uri,
    n.id AS node_id
FROM node n
WHERE n.kind LIKE 'cpp.%'
  AND n.properties->>'namespace' IS NOT NULL;
```

### Enrichment Interface (Future)

```csharp
/// <summary>
/// Optional post-parse enrichment. Implementations add edges, annotations,
/// or properties to Records produced by tree-sitter parsing.
/// </summary>
public interface ICppEnricher
{
    /// <summary>
    /// Whether this enricher can operate on the current environment.
    /// For libclang: true if headers are available.
    /// </summary>
    bool IsAvailable(RepoUri uri);

    /// <summary>
    /// Enrich parsed Records with additional information.
    /// Must not remove existing Records. May add edges, annotations,
    /// and new property keys to existing nodes.
    /// </summary>
    Task<EnrichmentResult> EnrichAsync(
        RepoUri uri,
        string content,
        Records existingRecords,
        CancellationToken ct);
}

public record EnrichmentResult(
    Edge[] AdditionalEdges,
    Annotation[] AdditionalAnnotations,
    Dictionary<Guid, Dictionary<string, string>> PropertyUpdates
);
```

This interface is designed but NOT implemented in the initial delivery. It exists to prove the architecture supports Option B without committing to its complexity. Enrichment runs per-file during the hot path (after tree-sitter parsing, before commit). Multi-file analysis is a separate stage that runs during idle processing.

**Note:** The current format loader contract (`IFormatLoader.LoadAsync` → `IFormatMaterializer.Materialize`) does not have an explicit post-materialize hook. Enrichment would be called within `LoadAsync` before returning the `DocumentModel`, or as a new pipeline stage between parsing and commit.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Tree-sitter as sole parser (initially) | Hybrid from day one | Zero-config path is non-negotiable. Hybrid adds ~37 MB per platform + header dependency. Ship something useful first. |
| Build grammar from master | Use released v0.23.4 | C++20 module syntax only on master. Pin to specific commit for stability. |
| Bundle native grammars ourselves | Contribute to TreeSitter.DotNet upstream | Upstream is unmaintained (last commit March 2022). Can't wait for or depend on external maintainers. |
| Heuristic macro detection | Precise macro expansion | Precise requires running the preprocessor (libclang). Heuristic with honest annotation satisfies "trustworthy or loudly not." |
| Qualified-name matching for header/source linking | Type-signature matching | Signature matching requires type resolution (libclang). Name matching handles the common case. False positives are rare in practice — overloaded functions across TUs are uncommon. |
| Content sniffing for `.h` | Requiring user configuration | Configuration violates "zero setup." Heuristic has acceptable failure modes — C++ grammar parses C correctly, C grammar misses C++ features. |

## Alternatives Considered

**clangd as primary parser:** Full compiler-grade analysis but multi-GB memory, requires project setup, 10-14s per-file preamble. Categorically incompatible with "runs on a developer laptop" and "time-to-usable." Considered for enrichment but even there the resource profile is excessive for a background process.

**CppAst.NET as primary parser:** Higher-level libclang wrapper. Simpler API than ClangSharp. But still requires headers for meaningful output, and without headers the `KeepGoing` flag produces silently incomplete ASTs. Better as a future enricher than a primary parser.

**Fork TreeSitter.DotNet:** Considered forking to add C++ grammar. Rejected because the upstream's architecture (submodules per grammar) would require maintaining the fork indefinitely. Bundling grammars ourselves is cleaner and independent of upstream decisions.

**ANTLR4 C++ grammar:** 71% parse success rate. Below threshold.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| TreeSitter.DotNet runtime can't load externally-built grammars | Validation experiment #1 from research. If it fails, evaluate TreeSitter.Bindings or build minimal P/Invoke layer. Fallback exists. |
| Tree-sitter C++ parse quality too low for real codebases | Validation experiment #2. If Qt/Windows SDK files produce >30% ERROR-contaminated structure, the macro detection heuristic degrades. Mitigation: per-file "parse confidence" score exposed in annotations. |
| Grammar builds fail on some platforms | CI/CD builds for all 6 platform targets (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64). Blocked platforms ship without C/C++ until fixed. |
| Qualified-name matching produces false positives | Log matches with low confidence. In practice, C++ qualified names are highly specific (`ns::Class::method`). |
| `.h` content sniffing misclassifies files | Consequence is minor (see Classification section). Add escape hatch: `.repoql/config` can override per-path classification. |
| Macro detection heuristic produces false positives | Annotation severity is `info`, not `error`. False "macro interference" warnings are low-harm compared to silently missing structure. |

## Extension Points

- `ICppEnricher` — Add libclang enrichment, compilation database parsing, or heuristic type inference without changing the core loader
- `cpp_views.sql` — Add new SQL views for C/C++ query patterns as needs emerge
- `MacroInterferenceDetector` known families list — Extensible for project-specific macros via configuration
- Grammar pinning — Update grammar commits independently of the loader codebase

---

## Dependencies

| Dependency | Version | License | Purpose |
|------------|---------|---------|---------|
| TreeSitter.DotNet | 1.3.0 | MIT | Tree-sitter runtime (already in codebase via Ruby) |
| tree-sitter-cpp | master (pinned SHA) | MIT | C++ grammar — built from source. Handles both C and C++ files. |

No new NuGet package dependencies beyond what the codebase already uses. The grammar native libraries are build artifacts, not package dependencies.

---

*Build the zero-config path first. Make it honest about what it can't see. Design the enrichment interface so the semantic path is always one implementation away.*
