# Proposal: First-class C# Format Support in RepoQL

## Summary
Bring Roslyn-backed parsing, source-generator-aware graph projection, and analyzer diagnostics for `*.cs` files into RepoQL so agents can query C# structure and run policy-complete linting. The work extends `RepoQL.Formats.DotNet` with a C# format descriptor plus a long-lived Roslyn workspace host that executes generators/analyzers once per project (then slices results per file) while keeping all integrations inside the format assembly and the existing RepoQL schema/CLI surface.

## Background
- RepoQL today understands `.csproj` and `.sln` files (see `src/Formats/RepoQL.Formats.DotNet`), but individual `.cs` files fall back to the plain-text handler, so there is no graph, X-ray, or lint data for the actual C# code.
- Users therefore cannot answer questions such as “which services implement `IPaymentProcessor`?”, “what public APIs are exported from this repo?”, or “which analyzers fail in CI?” without leaving RepoQL.
- The design snippet provided (Roslyn realtime lint host) aligns with RepoQL’s goals—load a project once, apply edits incrementally, respect `.editorconfig`, and emit deterministic diagnostics—but needs to be adapted to RepoQL’s batch enrichment pipeline, graph store, and annotation contracts.

## Goals
1. Register `text/plain;kind=code.csharp` as a first-class format with loader, analyzer, and materializer so C# files (including generator outputs) produce document nodes, X-ray strings, and graph records.
2. Collect namespaces, types, members, attributes, using directives, inheritance edges, and symbol references per file with deterministic `span_id`s and stable `symbol_key`s derived from Roslyn `SymbolKey`; merge partial types/members by key.
3. Execute Roslyn compiler diagnostics plus the project's analyzer set (NetAnalyzers, StyleCop, Roslynator, contract DLLs) with severity taken from `.editorconfig`/policy overlays—running analyzers once per project, slicing diagnostics per syntax tree.
4. Surface C# symbols, usages, and diagnostics through the existing `node`, `edge`, `span`, and `annotation` tables so the CLI, macros, and other RepoQL contracts remain unchanged, with any format-specific query recipes documented alongside the format.

## Non-goals
- Building a full LSP, code fix workflow, or refactoring engine.
- Replacing IDE tooling; we mirror what projects already configure.
- Modeling cross-project symbol resolution beyond “file-local” relationships in this iteration (multi-project queries can still be done via SQL joins).
- Supporting F# or VB.NET (future formats can reuse the same host).

## Scope & Constraints
- Implementation lives inside `RepoQL.Formats.DotNet` (and its test assets); no CLI, MCP server, or query-surface changes are required.
- C# data flows through the established schema tables (`artifact`, `node`, `edge`, `span`, `annotation`) and existing macros/UDFs. We add new `node.kind` values via the format but do not add DuckDB migrations, helper macros, or indexes in the core data layer.
- Configuration and execution knobs reuse existing format/provider configuration mechanisms; the CLI automatically benefits from the richer data without new commands or flags.
- Discoverability is addressed through documentation and samples within the format project instead of product-wide surface area changes.

## Approach Overview

### 1. Format Surface (Loader + Descriptor)
- **Semantic media type**: `text/plain;kind=code.csharp; charset=utf-8`.
- **Assembly**: extend `RepoQL.Formats.DotNet` with:
  - `CSharpLoader : IFormatLoader, IFormatMaterializer`
  - `CSharpAnalyzer : IFormatAnalyzer`
- **Descriptor**: register `new FormatDescriptor(CSharpSemType, csharpLoader, csharpAnalyzer, csharpLoader, labels: ["csharp","cs"])` ahead of the plain-text fallback so `.cs` files resolve here.

### 2. CSharpLoader & Document State
- Parse via `CSharpSyntaxTree.ParseText` with deterministic options (`DocumentationMode.Parse`, `LanguageVersion.Preview`, `SourceCodeKind.Regular`), capturing the original `SourceText` for downstream stages.
- `CSharpInventoryWalker` emits `CSharpDocumentState` containing:
  - File metadata: digest (`xxh64`), size, repo/project paths, optional owning project(s), `is_generated` flag (false for source files).
  - `LineMap` array (start offsets) used to compute deterministic `span_id = xxh64(fileDigest + ":" + start + ":" + length)`.
  - Namespaces, types, members, attributes, usings, regions, each with `display_key` (fully-qualified name w/ arity) and placeholder `symbol_key` slots (filled once Roslyn semantic info is available).
  - Partial type tracking (list of part spans + hash) to merge later by `SymbolKey`.
  - `SymbolReferenceHints` solely for logging; actual `USES_SYMBOL` edges come from semantic binding.
- Loader exposes `DiscoverEmbedsAsync` (no-op) and surfaces `state.GeneratedDocuments` so generators can append to the same materialization pass.

### 3. Source Generators & Additional Files
- `CSharpWorkspaceHost` runs source generators (`project.GetCompilationAsync` + `compilation.WithAnalyzers(...).GetAnalysisResultAsync`) so diagnostics and emitted trees match IDE/CI.
- For every `GeneratorRunResult.GeneratedTrees` entry:
  - Create synthetic `DocumentModel` instances flagged `is_generated=true`, derive deterministic URIs (`repoql://generated/<project>/<generator>/<hint>.cs`), and send them through the same loader/materializer/analyzer path.
  - Include generator diagnostics in RepoQL annotations.
- Additional files (`AdditionalTexts`) are exposed to analyzers and tracked for cache invalidation (changes bump the `(ProjectVersion, AnalyzerSet)` key).

### 4. Materialization & Graph Projection
- Liquid templates (`Templates/xray/csharp-*`) render headline/summary/structure following `docs/XRay.md`, tagging when content is generator-produced or partial.
- Graph emission rules:
  - **Nodes** (`document`, `csharp.namespace`, `csharp.type`, `csharp.member`, `csharp.attribute`, `csharp.using`, `csharp.generated_document`) store `symbol_key` (Roslyn `SymbolKey.ToString()`), `display_key`, `is_generated`, accessibility, modifiers, metrics, and normalized signatures (parameters JSON).
  - **Edges** `HAS_PART`, `DECLARES_SYMBOL`, `INHERITS_FROM`, `IMPLEMENTS`, `ANNOTATED_WITH`, `USES_SYMBOL` include `from_span_id` and `to_symbol_key`. Members always point to the merged partial-type node keyed by `SymbolKey`.
  - **Spans** rely on the deterministic `span_id` plus persisted `line_map` for lossless conversions.
- Data lands in the existing `node`, `edge`, `span`, and `annotation` tables. `symbol_key` and other C# metadata live in `properties` JSON so no DuckDB migrations, helper views, or global indexes are required beyond format-owned vocabulary entries.

### 5. Project Execution Model & Caching
- `CSharpProjectLocator` maps files → owning project(s) by scanning `.sln`/`.csproj`, reading `Project.Documents`, and falling back to nearest-upward `.csproj` heuristics.
- **Analyzer execution**: for each project, load once into an `MSBuildWorkspace`, run source generators, then execute `CompilationWithAnalyzers` **a single time**. Slice diagnostics per `SyntaxTree` via `result.GetSyntaxDiagnostics(tree)` / `GetSemanticDiagnostics` / `GetDeclarationDiagnostics` / `result.CompilationDiagnostics` filtered by `tree`.
- **Cache scope**: analyzer results keyed by `(ProjectId, ProjectVersion, AnalyzerFingerprint, GeneratorFingerprint)`; hitting the cache reuses diagnostics + generator outputs. Analyzer instances are reused, but `CompilationWithAnalyzers` objects are not shared across compilations.
- Cap concurrent project analyses to `min(Environment.ProcessorCount / 2, 4)` to keep resident memory ≤2 GB. Dispose generator outputs after graph emission to free Roslyn trees.

### 6. Symbol Binding & `USES_SYMBOL`
- For each document with an owning project, request the Roslyn `SemanticModel` and resolve declarations plus references:
  - Declarations: store `symbol_key = symbol.GetSymbolKey().ToString()` on nodes; merge partials by this key.
  - References: use `SemanticModel.GetSymbolInfo` / `GetDeclaredSymbol` / `GetTypeInfo`. When the symbol resolves, emit `USES_SYMBOL` edges with `to_symbol_key`. When resolution fails (file outside project), either skip or mark as `status="unresolved"` in edge metadata.
- If a file has no project, skip semantic edges and diagnostics (record `analysis_mode = "fast"` for transparency).

### 7. Analyzer Pipeline & Isolation
- Analyzer assemblies load into a dedicated `AssemblyLoadContext`; contracts can flip a flag to host analyzers out-of-proc (default for untrusted bundles). Each analyzer run is bounded by per-rule and per-project timeouts; misbehaving analyzers are quarantined and surfaced in telemetry.
- `.editorconfig`, AdditionalFiles, AnalyzerReferences, and policy overlays flow directly from the MSBuild workspace. RepoQL applies severity overrides only after Roslyn honors config.
- Diagnostics convert to `AnalysisResult` (`Kind=lint`, `Source=dotnet/roslyn`, `RuleId`, `Severity`, `Message`, `Target.SpanId`, `Data` containing `category`, `helpLink`, `project_path`, `is_generated`, `symbol_key`). SARIF fragments retain rule metadata and help links.


### 8. Telemetry & Observability
- Reuse the existing `IndexingMetrics` surface from inside `RepoQL.Formats.DotNet` by tagging Roslyn-specific measurements (project open, generator run, analyzer duration, cache hits) without requiring new shared counters or CLI plumbing.
- Structured logs remain inside the format assembly (one warning per failed project load with remediation hints). Existing RepoQL logging sinks pick them up automatically.

## Data Representation (within existing schema)
- **Nodes**: introduce new `node.kind` values (`csharp.namespace`, `csharp.type`, `csharp.member`, `csharp.attribute`, `csharp.using`, `csharp.generated_document`) emitted by the format. Metadata such as `symbol_key`, `display_key`, `is_generated`, `analysis_mode`, signatures, and attribute lists live inside the node's `properties` JSON, so no schema changes are required.
- **Edges**: reuse `HAS_PART` plus reference types like `DECLARES_SYMBOL`, `INHERITS_FROM`, `IMPLEMENTS`, `ANNOTATED_WITH`, and `USES_SYMBOL`. Edge-level metadata (`from_span_id`, `to_symbol_key`, `status`) also sits inside `properties`.
- **Spans**: the format keeps emitting deterministic `span_id`s and `line_map` data using existing columns.
- **Annotations**: diagnostics and metadata leverage the established `annotation` table (`kind='lint'|'metadata'`, `data.symbol_key`, `data.project_path`, etc.), making them automatically visible through `annotations_*` macros and CLI commands without any new report surfaces.
- **Querying**: consumers continue to use existing macros (`xray_*`, `annotations_*`, `entities_by_uri`) and can filter by the new `node.kind` strings or JSON properties. The format documentation will provide representative SQL snippets instead of adding shared DuckDB views.

## Execution Model & Performance Plan
1. **Discovery**: enumerate `.cs` files, map to owning project(s), enqueue per project group.
2. **Structure pass**: parse every `.cs` file (parallel, limited by I/O) to produce `CSharpDocumentState`, X-ray strings, and base graph nodes (without semantic data yet).
3. **Project analysis pass**:
   - For each project (up to `min(EnvProc/2, 4)` concurrent), open MSBuild workspace, run generators, execute analyzers once, cache `(ProjectVersion, AnalyzerFingerprint)`.
   - Apply semantic data (symbol keys, inheritance, usages) and diagnostics by slicing Roslyn results per `SyntaxTree`.

Performance targets:
- Cold project open: ≤15 s (one-time per project).
- Warm analyzer pass: 0.5–2.0 s P95 per project for full diagnostics, 50–200 ms for syntax+compiler-only runs.
- Memory budget: ≤2 GB by throttling concurrent compilations, disposing generator outputs promptly, and clearing Roslyn caches after each project.
- Throughput: analyzer results reused for subsequent files within the same project snapshot; no per-file re-execution.

## Open Decisions
1. **Analyzer isolation default**: we recommend in-proc `AssemblyLoadContext` for trusted repos and out-of-proc host for untrusted contracts; confirm default posture.
2. **Multi-project ownership**: for files included by multiple projects, diagnostics/graph rows will duplicate per project and include `project_path`. Validate this data shape with downstream consumers.

## Implementation Plan
1. **Foundations & packages**
   - Add `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.Build.Locator`, `Microsoft.CodeAnalysis.NetAnalyzers`, and analyzer isolation dependencies to `RepoQL.Formats.DotNet`.
   - Document Roslyn + MSBuild prerequisites in `docs/native-aot-tool-packaging.md` and `TOOLS.md`.
2. **Loader, X-ray & deterministic spans**
   - Implement `CSharpLoader`, `CSharpInventoryWalker`, `CSharpDocumentState`.
   - Generate `span_id`s from `(fileDigest,start,length)`, persist `line_map`, and capture placeholder `symbol_key` slots.
   - Author Liquid templates under `Templates/xray/csharp-*`; update `docs/XRay.md`, `docs/SemanticMediaType.md`, `docs/Vocabulary.md`.
3. **Graph & schema**
   - Emit nodes/edges with `symbol_key`, `display_key`, `is_generated`, `parameters_json`, etc.
   - Keep data inside the existing `artifact/node/edge/span/annotation` tables by storing C# metadata in `properties` JSON; document query patterns (examples, cookbook snippets) within `RepoQL.Formats.DotNet` docs rather than altering shared schema docs or macros.
4. **Workspace host, generators & caching**
   - Build `DotNetProjectCatalog`, `CSharpWorkspaceHost`, `CSharpAnalysisService`.
   - Implement per-project execution: load MSBuild, run source generators, execute `CompilationWithAnalyzers` once, cache on `(ProjectVersion, AnalyzerFingerprint, GeneratorFingerprint)`.
   - Surface configuration knobs (via the existing format/provider settings) for concurrency and analyzer isolation (ALC vs out-of-proc).
5. **Semantic binding & diagnostics**
   - Use Roslyn `SemanticModel` to assign `SymbolKey`s, merge partial types, and emit `USES_SYMBOL` edges only when resolved.
   - Convert diagnostics (including generator outputs) to `AnalysisResult`s with SARIF payloads, tagging `project_path`, `is_generated`.
6. **Docs & samples**
   - Add format-level documentation (e.g., `docs/formats/csharp.md`) describing available `node.kind` values, sample SQL snippets, and configuration knobs that plug into the existing format provider settings.
   - Provide sample repos/tests illustrating analyzer output and generator handling without touching CLI help or global docs.

## Testing Strategy
- **Unit**: structural walkers (namespace/type/member extraction), deterministic `span_id` generation, `.editorconfig` severity mapping, analyzer policy filtering, SymbolKey merge helpers, cache key computation.
- **Integration**:
  - Index sample repos covering ASP.NET, class libraries, and multi-targeted SDKs with analyzers + source generators + `AdditionalFiles`; assert diagnostics parity with `dotnet build` / `dotnet format`.
  - Verify generated documents materialize nodes/edges and diagnostics identical to IDE output.
  - Confirm `SymbolKey` remains stable across consecutive runs with identical code.
  - Validate multi-project ownership duplicates diagnostics per project and includes proper tags.
- **Resilience**: inject analyzer exceptions, hung analyzers, MSBuild load failures, and generator errors to confirm isolation, timeout, and quarantine logic.
- **Performance**: capture metrics for cold project open, warm analyzer runs, cache hit rate, and memory usage ≤2 GB. Include regression tests ensuring per-project analyzer execution occurs once per `project`.

## Risks & Mitigations
| Risk | Mitigation |
| --- | --- |
| MSBuild prerequisites missing | Detect SDK/version upfront, emit actionable warning, fall back to `Fast` mode rather than failing indexing. |
| Analyzer crashes / untrusted code | Load analyzers in custom `AssemblyLoadContext`, isolate via out-of-proc option for contracts needing sandboxing, honor timeout + quarantine list. |
| Large solutions exceeding memory budget | Limit concurrent `CompilationWithAnalyzers`, trim Roslyn caches after each project, allow configuration of max simultaneous projects. |
| Path mapping between RepoUri and Roslyn docs | Normalize paths via `RepoUri.LocalPath` + `Path.GetFullPath`, store both forms in `CSharpDocumentState`, add regression tests on Windows/Linux. |
| Schema visibility for new kinds | Clearly document the new `node.kind` values and JSON fields in the format docs; supply representative SQL so downstream consumers do not need schema migrations. |

## Deliverables & Acceptance Criteria
- `code.csharp` descriptor (loader/materializer/analyzer) registered in DI, with format-level documentation covering configuration and usage—no CLI or shared config changes required.
- DuckDB rows produced by the format carry `symbol_key`, `display_key`, `is_generated`, and deterministic `span_id`s inside existing columns/JSON, making the data immediately available through current macros/UDFs.
- Workspace host executes source generators + analyzers once per project, slices diagnostics per syntax tree, and emits RepoQL annotations + SARIF that match `dotnet build`/IDE output (including generator diagnostics).
- `SymbolKey` remains stable across two consecutive indexing runs with identical code; regression tests cover this guarantee.
- `USES_SYMBOL` edges only exist when Roslyn resolved the callee; unresolved references are omitted or marked `status="unresolved"`.
- Format-level metrics/logging plug into the existing RepoQL telemetry surfaces without requiring new CLI commands or dashboards.

Once these artifacts land, RepoQL agents can answer structural C# questions, enforce contract-specific analyzer policies, and surface consistent diagnostics across code and docs.

## Implementation Status (snapshot)
- ✅ Roslyn-backed `CSharpLoader` in `RepoQL.Formats.DotNet` parses `.cs` files, emits namespace/type/member nodes with deterministic spans, attaches `symbol_key` metadata, and produces `USES_SYMBOL` edges (including MSBuild-backed cross-file bindings when a project can be located).
- ✅ `CSharpWorkspaceHost` maintains a long-lived `MSBuildWorkspace`, loads each `.csproj` once, reuses the loader’s spans to map declarations back to the project syntax tree, and annotates the captured surface with real symbol keys, semantic references, and compiler/analyzer diagnostics so CI and RepoQL stay in lockstep—falling back to an in-memory compilation when no project is available.
- ✅ Source generators run as part of the workspace analysis pipeline, emitting virtual `repoql://generated/...` documents (flagged with `props.is_generated=true`) so generated code flows through the same loader/materializer/analyzer stack exactly once per project.
- ✅ `CSharpAnalyzer` surfaces the recorded diagnostics as RepoQL lint results (rule ids `csharp/<DiagId>`) while respecting per-rule overrides supplied through `AnalyzerSettings`.
- ✅ Embedded DuckDB helper views (`csharp_namespaces`, `csharp_types`, `csharp_members`) ship via the format's schema scripts so consumers can query C# data without custom SQL.
- ✅ `CSharpLoaderTests` exercise loader/materializer output, schema-script availability, symbol edges, analyzer overrides, and the new project-aware diagnostics/reference flows.
