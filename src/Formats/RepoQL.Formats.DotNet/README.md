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

