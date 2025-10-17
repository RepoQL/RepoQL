# RepoQL.Formats.DotNet

.NET project and solution format handlers for RepoQL.

## C# Project Files (*.csproj)

C# project format handler. Mirrors the Markdown/Mermaid patterns:

- Media type: `text/xml;kind=dotnet.csproj`
- Loader parses SDK, TargetFramework(s), OutputType, pack flags, `PackageReference`s and `ProjectReference`s.
- Materializer emits one `document` node per project with x‑ray fields (headline, summary, structure) rendered by Liquid templates.
- Composition children via `HAS_PART`:
  - `dotnet.tfm` — one per target framework (`props.tfm`)
  - `nuget.package` — one per package (`props.id`, `props.version?`)
  - `dotnet.project_reference` — one per project reference (`props.include`)
- Analyzer `csproj/unpinned-package` warns on missing or floating package versions.

## X‑ray Templates

Embedded templates (Liquid/Fluid) under `Templates/xray`:
- `headline.liquid` — concise, grep‑friendly line with size, sdk, output type, pack, tfm, counts.
- `summary.liquid` — key facts (SDK, OutputType, Pack), TFMs, top packages and project refs.
- `structure.liquid` — outline listing TFMs, packages, and references (with truncation).

Model keys available to templates:
- `file_name`, `size_bytes`, `sdk`, `output_type`, `pack`
- `tfms` (array), `tfm_text` (string)
- `packages` (array of `{ id, version? }`), `package_count`
- `project_refs` (array of `{ include }`), `project_ref_count`

## Node kinds
- `document` — the csproj itself (props include `sdk`, `tfms`, `output_type`, `pack`)
- `dotnet.tfm` — target frameworks
- `nuget.package` — packages
- `dotnet.project_reference` — project references

## Registration
Registered in DI with:
- `FormatDescriptor(text/xml;kind=dotnet.csproj, labels=["csproj"])`
- Templating wired via `AddLiquidTemplatingFromEmbedded` for this assembly.

## Tests
See `CsProjXrayTests` and `CsProjVariantsTests` in `RepoQL.Tests` for end‑to‑end coverage of x‑ray, props, and analyzer behavior.

---

## Solution Files (*.sln)

Visual Studio solution format handler:

- Media type: `text/plain;kind=dotnet.sln`
- Loader parses format version, Visual Studio version, projects, solution folders, configurations, and nested project mappings.
- Materializer emits one `document` node per solution with x‑ray fields (headline, summary, structure) rendered by Liquid templates.
- Composition children via `HAS_PART`:
  - `dotnet.solution_folder` — virtual folders in the solution (`props.name`, `props.guid`)
  - `dotnet.solution_project` — project references (`props.name`, `props.path`, `props.guid`, `props.type_guid`)

### X‑ray Templates

Embedded templates (Liquid/Fluid) under `Templates/xray`:
- `headline-sln.liquid` — concise, grep‑friendly line with size, format version, VS version, counts.
- `summary-sln.liquid` — key facts (Format, VS version, project/folder/config counts).
- `structure-sln.liquid` — outline listing folders with project counts, projects with paths, and configurations (with truncation).

Model keys available to templates:
- `file_name`, `size_bytes`, `format_version`, `vs_version`
- `project_count`, `folder_count`, `config_count`
- `projects` (array of `{ name, path, guid }`), `folders` (array of `{ name, guid }`)
- `configs` (array of strings like "Debug|Any CPU")
- `projects_display`, `folders_display`, `configs_text` (formatted strings)

### Node kinds
- `document` — the sln itself (props include `format_version`, `vs_version`, `project_count`, `folder_count`, `config_count`)
- `dotnet.solution_folder` — solution folders
- `dotnet.solution_project` — project references

### Registration
Registered in DI with:
- `FormatDescriptor(text/plain;kind=dotnet.sln, labels=["sln"])`
- Templating wired via `AddLiquidTemplatingFromEmbedded` for this assembly.

### Tests
See `SlnXrayTests` and `SlnVariantsTests` in `RepoQL.Tests` for end‑to‑end coverage of x‑ray and parsing behavior.


---

## C# Source Files (*.cs)

> Planned Roslyn-backed format for individual C# files. This section documents the shape of the feature so other producers/consumers can prepare.

- Media type: `text/plain;kind=code.csharp`
- Loader: builds Roslyn syntax tree + semantic model, captures facts into `CSharpDocumentState` (document id, digest, namespace/type/member inventories, metrics).
- Materializer: renders progressive X-ray strings (Liquid templates under `Templates/xray/csharp-*`) and emits graph records for namespaces, types, members, attributes, and symbol references.
- Diagnostics: execute the project's Roslyn analyzers through the MSBuild workspace so RepoQL surfaces the same diagnostics developers see in IDE/CI (respecting AnalyzerReferences and .editorconfig severity).

### X-ray Templates

- **headline** — single line such as  
  `PaymentService.cs | namespace Contoso.Payments | 1 class, 2 interfaces, 5 public methods`
- **summary** — YAML-style 8-12 line block listing namespaces, public types (kind/heritage), injected services, async API counts, XML doc coverage.
- **structure** — hierarchical outline:
  ```
  namespace Contoso.Payments
    public class PaymentService : IPaymentService, IDisposable
      ctor(IPaymentGateway gateway, IRepository repo)
      public Task<PaymentResult> ProcessAsync(PaymentRequest request)
      public Task<RefundResult> RefundAsync(Guid paymentId)
      private Task PublishEventAsync(PaymentEvent evt)
  ```
  Includes regions/partial indicators and notes async/static status.

### Graph Projection

- Nodes:
  - `document` — `.cs` file (`props`: media type, namespace_count, public_type_count, xml_doc_ratio)
  - `csharp.namespace` — logical namespace segments (`props`: name, qualified_name, line_span)
  - `csharp.type` — classes/structs/interfaces/records/enums (`props`: name, kind, accessibility, base_type, interface_list, generic_arity, partial, is_static)
  - `csharp.member` — methods/properties/fields/events (`props`: name, kind, accessibility, return_type, static, async, parameters json)
  - `csharp.attribute` — attribute applications (`props`: type_name, arguments_raw)
- Edges:
  - `HAS_PART` for containment (document->namespace->type->member) with ordinal preserving declaration order
  - `DECLARES_SYMBOL` linking document to top-level symbols
  - `IMPLEMENTS` and `INHERITS_FROM` for file-local inheritance/interfaces
  - `ANNOTATED_WITH` between symbols and attribute nodes
  - `USES_SYMBOL` capturing intra-file references resolved via Roslyn (best-effort)
- Spans: every node carries `span_id` with line/column offsets derived from `DocumentModel.LineMap`.

### Views

Helper SQL views planned in the DuckDB schema (prefix `view_csharp_*`), projecting the canonical tables:

- `view_csharp_types` — flattens type nodes with document URI, namespace, base/interface metadata, counts of members.
- `view_csharp_members` — exposes member signatures, modifiers, async/static flags, belonging type.
- `view_csharp_implements` — pairs deriving types and their interface targets for easy policy checks.
- Each view is additive and follows the stable naming guidance from `docs/Schema.md`.

### Diagnostics

RepoQL does not ship bespoke lint rules for C#; instead it reuses the analyzers already referenced by each project. During enrichment we
run Roslyn's `CompilationWithAnalyzers` over the MSBuild workspace so any analyzer DLLs (packages, `<Analyzer>` entries, .NET SDK defaults) execute as they would in IDE/CI. Key properties:

- `.editorconfig` is honored: severities, suppressed IDs, and custom rule options flow directly from the project configuration.
- Analyzer-generated diagnostics appear as RepoQL `lint` annotations with rule id, message, severity, and precise span anchors.
- When analyzers expose code fixes, we translate them into `AnalysisFix` payloads so RepoPatch can apply safe edits.
- Diagnostics are scoped to real project files; Markdown embedded C# blocks fall back to lightweight syntax checks (we surface them as hints without invoking full project analyzers, because they lack compilation context).

This approach keeps RepoQL in sync with the developer's toolchain while preserving the option to add RepoQL-specific cross-file analyzers later if needed.

### Registration

Once shipped, services will register:

- `FormatDescriptor(text/plain;kind=code.csharp, labels=["csharp","cs"])`
- Liquid templates via `AddLiquidTemplatingFromEmbedded(typeof(CSharpLoader).Assembly, "RepoQL.Formats.DotNet.Templates.CSharp")`
- Roslyn services (workspace, metadata reference resolver) as singletons
- Analyzer added to DI and to the format registry so Markdown embeddings resolve the same descriptor

### Tests

New suites under `RepoQL.Tests`:

- `CSharpXrayTests` — validates headline/summary/structure content across sample files (class library, partial classes, record types).
- `CSharpGraphTests` — asserts node/edge/span output for namespaces, inheritance, attributes.
- `CSharpDiagnosticsTests` — integration tests that run standard Roslyn analyzers (e.g., CA1815) through the RepoQL pipeline and assert `.editorconfig` severity is respected.
- `MarkdownEmbeddedCSharpTests` — validates the fallback syntax hints for embedded fences and that we do not surface project analyzer results when compilation context is missing.

---
