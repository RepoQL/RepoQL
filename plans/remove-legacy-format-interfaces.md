# Plan: Remove Legacy Format Interfaces

## Summary

Migrate from the legacy `IFormatLoader`/`IFormatMaterializer`/`FormatDescriptor` pattern to a unified `IAsyncPipeline` architecture where all format processing flows through the indexing pipeline.

## Current State

| Interface | Status | Purpose |
|-----------|--------|---------|
| `IFormatLoader` | Legacy | `LoadAsync`, `CanLoadAsync`, `GetSchemaScripts`, `DiscoverEmbedsAsync` |
| `IFormatMaterializer` | Legacy | `Materialize`, `Supports` |
| `FormatDescriptor` | Legacy | Bundles loader+analyzer+materializer for registration |
| `IFormatRegistry` | Legacy | Lookup formats by media type/label |
| `AnalysisWorkspace` | Legacy | On-demand document loading via registry |

**Already removed** (completed):
- `CSharpParser`, `MarkdownParser`, `TypeScriptParser` wrapper classes
- `MarkdownAnalysisProcessor`

## End State Architecture

### Pipeline-Centric Model

All format processing flows through `IAsyncPipeline<TInput, TResult>`:

```
File Discovery
      ↓
IDiscoveredArtifact
      ↓
ClassificationPipeline ──→ IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
      ↓
IClassifiedArtifact
      ↓
ParsingPipeline ──→ IAsyncPipeline<IClassifiedArtifact, Records?>
      ↓
IParsedArtifact
      ↓
AnalysisPipeline ──→ IAsyncPipeline<IParsedArtifact, Annotation[]>
      ↓
IAnnotatedArtifact
```

### Format Registration Pattern

```csharp
public static IServiceCollection AddMarkdownFormat(this IServiceCollection services)
{
    // Schema provider (SQL views)
    services.AddSingleton<IFormatSchemaProvider, MarkdownSchemaProvider>();

    // Pipeline processors
    services.AddIndexingProcessor<MarkdownClassifier>();
    services.AddIndexingProcessor<MarkdownParser>();
    services.AddIndexingProcessor<MarkdownAnalyzer>();

    return services;
}
```

### Parser Implementation

Each parser implements `IAsyncPipeline<IClassifiedArtifact, Records?>` with loading and materializing inline:

```csharp
public class MarkdownParser : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records?, PipelineResult)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (item.MediaType?.Kind != "markdown.doc")
            return await next(item);

        var document = await LoadMarkdownAsync(item, token);
        var records = MaterializeMarkdown(document);
        return (records, PipelineResult.Success);
    }
}
```

### Schema Scripts

New interface to handle the `GetSchemaScripts()` loose end:

```csharp
public interface IFormatSchemaProvider
{
    IEnumerable<FormatSqlScript> GetSchemaScripts();
}

// Collection at startup
var scripts = serviceProvider
    .GetServices<IFormatSchemaProvider>()
    .SelectMany(p => p.GetSchemaScripts());
```

## What Gets Removed

- `IFormatLoader` - absorbed into Parser implementations
- `IFormatMaterializer` - absorbed into Parser implementations
- `FormatDescriptor` - replaced by direct processor registration
- `IFormatRegistry` - no longer needed
- `AnalysisWorkspace` - refactored or removed

## What Gets Added

- `IFormatSchemaProvider` - SQL view registration

## Migration Phases

### Phase 1: Create `IFormatSchemaProvider`
- New interface in `RepoQL.Contracts`
- Implement for each format (delegates to existing loader initially)

### Phase 2: Recreate Parser Classes
- `MarkdownParser` with inline loading/materializing
- `TypeScriptParser` with inline loading/materializing
- `CSharpParser` with inline loading/materializing
- Keep existing `PlainTextParser`

### Phase 3: Update Registrations
- Remove `FormatDescriptor` registrations
- Add `AddIndexingProcessor<*Parser>()` calls
- Add `IFormatSchemaProvider` registrations

### Phase 4: Refactor `AnalysisWorkspace`
- Remove dependency on `IFormatRegistry`
- Handle embedded fragment discovery via parsers

### Phase 5: Delete Legacy Interfaces
- `IFormatLoader`
- `IFormatMaterializer`
- `FormatDescriptor`
- `IFormatRegistry`

### Phase 6: Clean Up Loaders
- Merge `*Loader` logic into `*Parser` classes
- Or rename to internal helpers

## Files Affected

**Delete:**
- `src/RepoQL.Contracts/IFormatLoader.cs`
- `src/RepoQL.Contracts/IFormatMaterializer.cs`
- `src/RepoQL.Contracts/FormatDescriptor.cs`
- `src/RepoQL.Contracts/IFormatRegistry.cs`
- `src/RepoQL.Core/FormatRegistry.cs`
- `src/RepoQL.Core/AnalysisWorkspace.cs` (or refactor)

**Create:**
- `src/RepoQL.Contracts/IFormatSchemaProvider.cs`

**Modify:**
- All `*Loader` classes (merge into parsers or make internal)
- All `*ServiceCollectionExtensions.cs` files
- `RepoIndexerServiceCollectionExtensions.cs`
- `SingleThreadedDatabaseWriter.cs` (schema script collection)
- `IndexedRepoBuilder.cs` (test harness)

## Open Questions

1. **Merge Loader into Parser or keep separate?**
   - Option A: Parser contains all logic (simpler, less indirection)
   - Option B: Parser wraps Loader (preserves existing code structure)

2. **Embedded fragment discovery?**
   - Currently in `IFormatLoader.DiscoverEmbedsAsync()`
   - Move to separate interface or into analyzer?

3. **On-demand document loading?**
   - `AnalysisWorkspace` provides this for analyzers
   - Could inject parser directly or remove capability
