# RepoQL.Tool Native AOT Packaging

RepoQL.Tool now ships as a multi-targeted .NET tool so older SDKs can fall back to a framework-dependent runner while .NET 10+ machines consume Native AOT binaries when available.

## Project Configuration Highlights
- `RepoQL.ConsoleApp` packs as a tool with `TargetFrameworks` `net9.0;net10.0` when no runtime identifier is supplied.
- Native AOT publish settings apply only to RID-specific builds (Linux, Windows, macOS). The `any` RID produces a framework-dependent fallback build.
- All library projects in the dependency graph now target both `net9.0` and `net10.0` to satisfy the RID-specific AOT inner builds.

## Pack Workflow
Run the pack commands from the repository root. Each invocation writes packages to `artifacts/` so you can gather them for publication.

```bash
# Root meta-package: no RID, multi-TFM runner for legacy SDKs
dotnet pack src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj \
  -c Release \
  -o artifacts/tool/root

# RID-specific Native AOT packages (one per OS/arch)
for rid in linux-x64 win-x64 win-arm64 osx-arm64; do
  dotnet pack src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj \
    -c Release \
    -r "$rid" \
    -o "artifacts/tool/native"
done

# Framework-dependent fallback when no RID match is found
dotnet pack src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj \
  -c Release \
  -r any \
  -o artifacts/tool/native
```

## Notes & Caveats
- These commands require the .NET 10 SDK (10.0.100 or newer) because RID-specific tool packages ship with .NET 10.
- Native AOT publishing needs to run on the target operating system. Build the RID-specific packages on matching OS runners (for example via a CI matrix).
- When running in a restricted environment, a `dotnet restore` may fail if NuGet feeds cannot be reached. Publish steps assume network access to `api.nuget.org`.
- The GitHub Actions workflow (`.github/workflows/native-aot-tool.yml`) publishes each `.nupkg` to the GitHub Packages NuGet feed at `https://nuget.pkg.github.com/<owner>/index.json` whenever `main` is pushed. Ensure repository permissions allow `GITHUB_TOKEN` to write packages or provide a custom PAT via repository secrets.
- `linux-arm64` is temporarily excluded because the GitHub-hosted runners do not provide the required cross-linker tooling (`ld.bfd`), so that package will be added once a suitable builder is available.
