# RepoQL.Formats.DotNetProject

C# project (*.csproj) format handler for RepoQL. Mirrors the Markdown/Mermaid patterns:

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

