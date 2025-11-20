using System.IO;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.DotNet;

public class CSharpClassifier(ILogger<CSharpClassifier>? logger = null)
    : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    private static readonly SemanticMediaType MediaType = SemanticMediaType
        .Create("text", "plain")
        .WithKind(CSharpLoader.MediaKind);

    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        var extension = Path.GetExtension(item.Name).ToLowerInvariant();

        if (extension is ".cs")
        {
            logger?.LogDebug("Classified {Uri} as {Kind}", item.Uri, MediaType.Kind);
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((MediaType, PipelineResult.Success));
        }

        return next(item);
    }
}
