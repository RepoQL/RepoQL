using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Go;

public sealed class GoClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (GoMediaTypes.TryResolve(item.Name, out var mediaType))
        {
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
        }

        return next(item);
    }
}
