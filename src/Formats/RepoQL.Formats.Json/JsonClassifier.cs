using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Json;

/// <summary>
/// Classifier for JSON family files.
///
/// Purpose: Claims JSON extensions during discovery and assigns a stable semantic media type.
///
/// Complexity: None. Extension-based routing only.
/// </summary>
public sealed class JsonClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((JsonMediaTypes.Json, PipelineResult.Success));

        if (item.Name.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((JsonMediaTypes.Json, PipelineResult.Success));

        if (item.Name.EndsWith(".json5", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((JsonMediaTypes.Json, PipelineResult.Success));

        if (item.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((JsonMediaTypes.Json, PipelineResult.Success));

        if (item.Name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((JsonMediaTypes.Json, PipelineResult.Success));

        return next(item);
    }
}
