using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Terraform;

public sealed class TerraformClassifier(ILogger<TerraformClassifier>? logger = null)
    : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        var extension = Path.GetExtension(item.Name).ToLowerInvariant();
        if (!TerraformMediaTypes.TryResolve(extension, out var mediaType))
        {
            return next(item);
        }

        logger?.LogDebug("Classified {Uri} as {Kind}", item.Uri, mediaType!.Kind);
        return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
    }
}
