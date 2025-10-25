# Proposal: First-class C# Format Support in RepoQL

## Summary
Bring Roslyn-backed parsing, source-generator-aware graph projection, and analyzer diagnostics for `*.cs` files into RepoQL so agents can query C# structure, run policy-complete linting, and surface Markdown-embedded code issues. The work extends `RepoQL.Formats.DotNet` with a C# format descriptor, a long-lived Roslyn workspace host that executes generators/analyzers once per project (then slices results per file), and DuckDB schema/view updates that track `SymbolKey` identity, TFMs, and generated content.

## Background
- RepoQL today understands `.csproj` and `.sln` files (see `src/Formats/RepoQL.Formats.DotNet`), but individual `.cs` files fall back to the plain-text handler, so there is no graph, X-ray, or lint data for the actual C# code.
- Users therefore cannot answer questions such as “which services implement `IPaymentProcessor`?”, “what public APIs are exported from this repo?”, or “which analyzers fail in CI?” without leaving RepoQL.
- The design snippet provided (Roslyn realtime lint host) aligns with RepoQL’s goals—load a project once, apply edits incrementally, respect `.editorconfig`, and emit deterministic diagnostics—but needs to be adapted to RepoQL’s batch enrichment pipeline, graph store, and annotation contracts.
- Markdown already supports embedded analyzers; adding C# parsing unlocks consistent diagnostics for fenced ` ```csharp ` blocks by reusing the same descriptors.

## Goals
1. Register `text/plain;kind=code.csharp` as a first-class format with loader, analyzer, and materializer so C# files (including generator outputs) produce document nodes, X-ray strings, and graph records.
2. Collect namespaces, types, members, attributes, using directives, inheritance edges, and symbol references per file with deterministic `span_id`s and stable `symbol_key`s derived from Roslyn `SymbolKey`; merge partial types/members by key.
3. Execute Roslyn compiler diagnostics plus the project’s analyzer set (NetAnalyzers, StyleCop, Roslynator, contract DLLs) with severity taken from `.editorconfig`/policy overlays—running analyzers once per project, slicing diagnostics per syntax tree, and covering all target TFMs according to a documented selection policy.
4. Support Markdown-embedded C# blocks with syntax + compiler diagnostics by default, only attempting semantic binding when a repo project is explicitly mapped so docs get hints without false positives.
5. Ship helper DuckDB views/macros (`csharp_types`, `csharp_members`, `csharp_inheritance`, `csharp_usages`) plus indexes on `symbol_key`/`name` so RepoQL consumers can immediately slice C# data across TFMs and generated files.

## Non-goals
- Building a full LSP, code fix workflow, or refactoring engine.
- Replacing IDE tooling; we mirror what projects already configure.
- Modeling cross-project symbol resolution beyond “file-local” relationships in this iteration (multi-project queries can still be done via SQL joins).
- Supporting F# or VB.NET (future formats can reuse the same host).

## Approach Overview

### 1. Format Surface (Loader + Descriptor)
- **Semantic media type**: `text/plain;kind=code.csharp; charset=utf-8`.
- **Assembly**: extend `RepoQL.Formats.DotNet` with:
  - `CSharpLoader : IFormatLoader, IFormatMaterializer`
  - `CSharpAnalyzer : IFormatAnalyzer`
  - `CSharpEmbeddedAnalyzer : IEmbeddedAnalyzer`
- **Descriptor**: register `new FormatDescriptor(CSharpSemType, csharpLoader, csharpAnalyzer, csharpLoader, labels: ["csharp","cs"])` ahead of the plain-text fallback so `.cs` files and Markdown embeddings resolve here.

### 2. CSharpLoader & Document State
- Parse via `CSharpSyntaxTree.ParseText` with deterministic options (`DocumentationMode.Parse`, `LanguageVersion.Preview`, `SourceCodeKind.Regular`), capturing the original `SourceText` for downstream stages.
- `CSharpInventoryWalker` emits `CSharpDocumentState` containing:
  - File metadata: digest (`xxh64`), size, repo/project paths, optional owning project(s), `tfm` selection, `is_generated` flag (false for source files).
  - `LineMap` array (start offsets) used to compute deterministic `span_id = xxh64(fileDigest + ":" + start + ":" + length)`.
  - Namespaces, types, members, attributes, usings, regions, each with `display_key` (fully-qualified name w/ arity) and placeholder `symbol_key` slots (filled once Roslyn semantic info is available).
  - Partial type tracking (list of part spans + hash) to merge later by `SymbolKey`.
  - `SymbolReferenceHints` solely for logging; actual `USES_SYMBOL` edges come from semantic binding.
- Loader exposes `DiscoverEmbedsAsync` (no-op) and surfaces `state.GeneratedDocuments` so generators can append to the same materialization pass.

### 3. Source Generators & Additional Files
- `CSharpWorkspaceHost` runs source generators (`project.GetCompilationAsync` + `compilation.WithAnalyzers(...).GetAnalysisResultAsync`) so diagnostics and emitted trees match IDE/CI.
- For every `GeneratorRunResult.GeneratedTrees` entry:
  - Create synthetic `DocumentModel` instances flagged `is_generated=true`, derive deterministic URIs (`repoql://generated/<project>/<tfm>/<generator>/<hint>.cs`), and send them through the same loader/materializer/analyzer path.
  - Include generator diagnostics in RepoQL annotations.
- Additional files (`AdditionalTexts`) are exposed to analyzers and tracked for cache invalidation (changes bump the `(ProjectVersion, AnalyzerSet, TFM)` key).

### 4. Materialization & Graph Projection
- Liquid templates (`Templates/xray/csharp-*`) render headline/summary/structure following `docs/XRay.md`, tagging when content is generator-produced or partial.
- Graph emission rules:
  - **Nodes** (`document`, `csharp.namespace`, `csharp.type`, `csharp.member`, `csharp.attribute`, `csharp.using`, `csharp.generated_document`) store `symbol_key` (Roslyn `SymbolKey.ToString()`), `display_key`, `tfm`, `is_generated`, accessibility, modifiers, metrics, and normalized signatures (parameters JSON).
  - **Edges** `HAS_PART`, `DECLARES_SYMBOL`, `INHERITS_FROM`, `IMPLEMENTS`, `ANNOTATED_WITH`, `USES_SYMBOL` include `from_span_id`, `to_symbol_key`, and `tfm`. Members always point to the merged partial-type node keyed by `SymbolKey`.
  - **Spans** rely on the deterministic `span_id` plus persisted `line_map` for lossless conversions.
- Schema updates add node/edge kinds, indexes on `(kind, props->>'symbol_key')` and `(kind, props->>'name')`, and helper views `csharp_types`, `csharp_members`, `csharp_inheritance`, `csharp_usages`.

### 5. Project Execution Model, TFMs & Caching
- `CSharpProjectLocator` maps files → owning project(s) by scanning `.sln`/`.csproj`, reading `Project.Documents`, and falling back to nearest-upward `.csproj` heuristics. Files referenced by multiple projects (different TFMs) inherit all owners.
- **Analyzer execution**: for each `(project, tfm)` tuple, load once into an `MSBuildWorkspace`, run source generators, then execute `CompilationWithAnalyzers` **a single time**. Slice diagnostics per `SyntaxTree` via `result.GetSyntaxDiagnostics(tree)` / `GetSemanticDiagnostics` / `GetDeclarationDiagnostics` / `result.CompilationDiagnostics` filtered by `tree`.
- **Multi-TFM policy**: default to a configurable “active TFM” rule (e.g., prefer `net8.0`, else highest stable). Contracts can opt into “analyze all TFMs” mode, in which case diagnostics and graph nodes are duplicated per TFM and tagged accordingly. Decision documented in `docs/SemanticMediaType.md` + proposal appendix.
- **Cache scope**: analyzer results keyed by `(ProjectId, ProjectVersion, AnalyzerFingerprint, GeneratorFingerprint, tfm)`; hitting the cache reuses diagnostics + generator outputs. Analyzer instances are reused, but `CompilationWithAnalyzers` objects are not shared across compilations.
- Cap concurrent project analyses to `min(Environment.ProcessorCount / 2, 4)` to keep resident memory ≤2 GB. Dispose generator outputs after graph emission to free Roslyn trees.

### 6. Symbol Binding & `USES_SYMBOL`
- For each document with an owning project, request the Roslyn `SemanticModel` and resolve declarations plus references:
  - Declarations: store `symbol_key = symbol.GetSymbolKey().ToString()` on nodes; merge partials by this key.
  - References: use `SemanticModel.GetSymbolInfo` / `GetDeclaredSymbol` / `GetTypeInfo`. When the symbol resolves, emit `USES_SYMBOL` edges with `to_symbol_key`. When resolution fails (file outside project), either skip or mark as `status="unresolved"` in edge metadata.
- If a file has no project, skip semantic edges and diagnostics (record `analysis_mode = "fast"` for transparency).

### 7. Analyzer Pipeline & Isolation
- Analyzer assemblies load into a dedicated `AssemblyLoadContext`; contracts can flip a flag to host analyzers out-of-proc (default for untrusted bundles). Each analyzer run is bounded by per-rule and per-project timeouts; misbehaving analyzers are quarantined and surfaced in telemetry.
- `.editorconfig`, AdditionalFiles, AnalyzerReferences, and policy overlays flow directly from the MSBuild workspace. RepoQL applies severity overrides only after Roslyn honors config.
- Diagnostics convert to `AnalysisResult` (`Kind=lint`, `Source=dotnet/roslyn`, `RuleId`, `Severity`, `Message`, `Target.SpanId`, `Data` containing `category`, `helpLink`, `project_path`, `tfm`, `is_generated`, `symbol_key`). SARIF fragments retain rule metadata and help links.

### 8. Markdown Semantics
- Markdown fences labeled `csharp`/`cs` route to `CSharpEmbeddedAnalyzer`.
- Default behavior: syntax tree + compiler diagnostics only (`Fast` mode). Semantic binding and analyzer execution happen **only** if the fence explicitly declares a repo project (frontmatter or directive) and RepoQL can map it; otherwise, we emit degraded hints annotated with `analysis_mode="fast-markdown"`.
- Span remapping translates snippet offsets back into the Markdown document; `target.repo_uri` always references the host Markdown file.

### 9. Telemetry & Observability
- Extend `IndexingMetrics` with instruments for: project open, generator run, analyzer execution time per rule, diagnostics count by severity/source, cache hit rate, memory high-water marks, Markdown fast-mode occurrences, analyzer quarantine events.
- Structured logs capture one warning per failed project load with remediation hints (install SDK, restore packages, etc.).
- Metrics feed dashboards plus CLI summaries (`repoql host lint --stats`).

### 10. CLI & Config Surface
- CLI: `repoql host lint --format csharp --project <path> [--tfm net8.0] [--mode fast|full] [--include-generated]` for validation and debugging.
- Environment/config flags:
  - `REPOQL_CSHARP_ANALYZERS=<path>` for extra analyzer DLLs.
  - `REPOQL_CSHARP_MAX_CONCURRENCY` to tune scheduler.
  - `REPOQL_CSHARP_TFM_SELECTION=auto|all|net8.0` to define active TFM strategy.
  - `REPOQL_CSHARP_ANALYZER_ISOLATION=alc|outofproc` to enforce the contract requirement.
  - `REPOQL_CSHARP_MARKDOWN_PROJECT_HINT=<path>` to opt Markdown fences into semantic mode.

## Data Model Updates
- **Nodes**: extend DuckDB schema with `symbol_key`, `display_key`, `tfm`, `is_generated`, `analysis_mode`, `parameters_json` (members), `attributes_json`, `partial_part_count`. These fields exist on `document`, `csharp.namespace`, `csharp.type`, `csharp.member`, `csharp.attribute`, `csharp.generated_document`.
- **Edges**: `DECLARES_SYMBOL`, `INHERITS_FROM`, `IMPLEMENTS`, `USES_SYMBOL` add `from_span_id`, `to_symbol_key`, `tfm`, and `status` (e.g., `resolved`, `unresolved`). `HAS_PART` order is preserved via `ordinal`.
- **Spans**: store deterministic `span_id` plus `line_map` per document and `start_line`, `end_line`, `start_offset`, `length` for nodes. Markdown remapping reuses these IDs.
- **Views & indexes**:
  - `csharp_types(symbol_key, document_uri, tfm, is_generated, name, accessibility, base_type, interfaces, member_count)`
  - `csharp_members(symbol_key, containing_symbol_key, tfm, is_generated, name, kind, signature_json, attributes_json)`
  - `csharp_inheritance(parent_symbol_key, child_symbol_key, relationship, tfm)`
  - `csharp_usages(from_symbol_key, to_symbol_key, tfm, edge_kind, is_generated)`
  - Indices on `(kind, props->>'symbol_key')`, `(kind, props->>'name')`, and `(kind, props->>'tfm')` to keep query costs predictable.

## Execution Model & Performance Plan
1. **Discovery**: enumerate `.cs` files, map to project(s)/TFMs, enqueue per project group.
2. **Structure pass**: parse every `.cs` file (parallel, limited by I/O) to produce `CSharpDocumentState`, X-ray strings, and base graph nodes (without semantic data yet).
3. **Project analysis pass**:
   - For each `(project, tfm)` (up to `min(EnvProc/2, 4)` concurrent), open MSBuild workspace, run generators, execute analyzers once, cache `(ProjectVersion, AnalyzerFingerprint, tfm)`.
   - Apply semantic data (symbol keys, inheritance, usages) and diagnostics by slicing Roslyn results per `SyntaxTree`.
4. **Markdown pass**: run `Fast` diagnostics for fenced blocks, optionally upgrading to semantic mode when configuration links them to a project.

Performance targets:
- Cold project open: ≤15 s (one-time per project/TFM).
- Warm analyzer pass: 0.5–2.0 s P95 per project/TFM for full diagnostics, 50–200 ms for syntax+compiler-only runs.
- Memory budget: ≤2 GB by throttling concurrent compilations, disposing generator outputs promptly, and clearing Roslyn caches after each project.
- Throughput: analyzer results reused for subsequent files within the same project snapshot; no per-file re-execution.

## Open Decisions
1. **TFM policy**: proposal defaults to `auto` (prefer explicit configuration, else latest `net*` TFM). Need stakeholder sign-off on whether “all TFMs” should be opt-in or default per contract.
2. **Analyzer isolation default**: we recommend in-proc `AssemblyLoadContext` for trusted repos and out-of-proc host for untrusted contracts; confirm default posture.
3. **Markdown semantic opt-in**: currently requires explicit mapping; confirm whether repos may opt into global semantic mode via config.
4. **Multi-project ownership**: for files included by multiple projects with divergent TFMs, diagnostics/graph rows will duplicate per `(project, tfm)` and include `project_path` + `tfm`. Validate this data shape with downstream consumers.

## Implementation Plan
1. **Foundations & packages**
   - Add `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.Build.Locator`, `Microsoft.CodeAnalysis.NetAnalyzers`, and analyzer isolation dependencies to `RepoQL.Formats.DotNet`.
   - Document Roslyn + MSBuild prerequisites in `docs/native-aot-tool-packaging.md` and `TOOLS.md`.
2. **Loader, X-ray & deterministic spans**
   - Implement `CSharpLoader`, `CSharpInventoryWalker`, `CSharpDocumentState`.
   - Generate `span_id`s from `(fileDigest,start,length)`, persist `line_map`, and capture placeholder `symbol_key` slots.
   - Author Liquid templates under `Templates/xray/csharp-*`; update `docs/XRay.md`, `docs/SemanticMediaType.md`, `docs/Vocabulary.md`.
3. **Graph & schema**
   - Emit nodes/edges with `symbol_key`, `display_key`, `tfm`, `is_generated`, `parameters_json`, etc.
   - Provide DuckDB migrations + helper views + indexes; update `schema.graphql`, `docs/Schema.Design.md`, and add a “C# queries” cookbook.
4. **Workspace host, generators & caching**
   - Build `DotNetProjectCatalog`, `CSharpWorkspaceHost`, `CSharpAnalysisService`.
   - Implement per-project `(project, tfm)` execution: load MSBuild, run source generators, execute `CompilationWithAnalyzers` once, cache on `(ProjectVersion, AnalyzerFingerprint, GeneratorFingerprint, tfm)`.
   - Surface configuration knobs for TFM selection, concurrency, analyzer isolation (ALC vs out-of-proc).
5. **Semantic binding & diagnostics**
   - Use Roslyn `SemanticModel` to assign `SymbolKey`s, merge partial types, and emit `USES_SYMBOL` edges only when resolved.
   - Convert diagnostics (including generator outputs) to `AnalysisResult`s with SARIF payloads, tagging `tfm`, `project_path`, `is_generated`.
6. **Markdown integration**
   - Implement `CSharpEmbeddedAnalyzer` with default syntax+compiler mode, semantic opt-in via config, and span remapping back to host Markdown.
7. **CLI, config & docs**
   - Ship `repoql host lint --format csharp` options (`--project`, `--tfm`, `--mode`, `--include-generated`) and environment variables documented in `docs/processes/implementation-process.md`.
   - Update telemetry dashboards + `docs/Vision.md` to reflect analyzer parity.

## Testing Strategy
- **Unit**: structural walkers (namespace/type/member extraction), deterministic `span_id` generation, `.editorconfig` severity mapping, analyzer policy filtering, Markdown span remapping, SymbolKey merge helpers, cache key computation.
- **Integration**:
  - Index sample repos covering ASP.NET, class libraries, and multi-targeted SDKs with analyzers + source generators + `AdditionalFiles`; assert diagnostics parity with `dotnet build` / `dotnet format`.
  - Verify generated documents materialize nodes/edges and diagnostics identical to IDE output.
  - Confirm `SymbolKey` remains stable across consecutive runs with identical code.
  - Validate multi-project ownership duplicates diagnostics per `(project, tfm)` and includes proper tags.
- **Resilience**: inject analyzer exceptions, hung analyzers, MSBuild load failures, and generator errors to confirm isolation, timeout, and quarantine logic.
- **Performance**: capture metrics for cold project open, warm analyzer runs, Markdown fast-mode latency, cache hit rate, and memory usage ≤2 GB. Include regression tests ensuring per-project analyzer execution occurs once per `(project, tfm)`.

## Risks & Mitigations
| Risk | Mitigation |
| --- | --- |
| MSBuild prerequisites missing | Detect SDK/version upfront, emit actionable warning, fall back to `Fast` mode rather than failing indexing. |
| Analyzer crashes / untrusted code | Load analyzers in custom `AssemblyLoadContext`, isolate via out-of-proc option for contracts needing sandboxing, honor timeout + quarantine list. |
| Large solutions exceeding memory budget | Limit concurrent `CompilationWithAnalyzers`, trim Roslyn caches after each project, allow configuration of max simultaneous projects. |
| Path mapping between RepoUri and Roslyn docs | Normalize paths via `RepoUri.LocalPath` + `Path.GetFullPath`, store both forms in `CSharpDocumentState`, add regression tests on Windows/Linux. |
| Schema churn for downstream queries | Version new node kinds explicitly, provide migration scripts and announce in release notes. |

## Deliverables & Acceptance Criteria
- `code.csharp` descriptor (loader/materializer/analyzer + Markdown embedded analyzer) registered in DI, with CLI + config surface documented.
- DuckDB stores `csharp.*` nodes/edges carrying `symbol_key`, `display_key`, `tfm`, `is_generated`, deterministic `span_id`s, and exposes helper views/indexes described in `docs/Schema.Design.md`.
- Workspace host executes source generators + analyzers once per `(project, tfm)`, slices diagnostics per syntax tree, and emits RepoQL annotations + SARIF that match `dotnet build`/IDE output (including generator diagnostics).
- `SymbolKey` remains stable across two consecutive indexing runs with identical code; regression tests cover this guarantee.
- `USES_SYMBOL` edges only exist when Roslyn resolved the callee; unresolved references are omitted or marked `status="unresolved"`.
- Markdown fences default to syntax+compiler diagnostics and explicitly label degraded mode; semantic mode requires configuration.
- Telemetry dashboards show project open/generator/analyzer timings, cache hit rate, analyzer quarantines, and memory high-water marks.

Once these artifacts land, RepoQL agents can answer structural C# questions, enforce contract-specific analyzer policies, and surface consistent diagnostics across code and docs.
