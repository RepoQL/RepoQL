using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Indexing.Pipelines.Classification;

public class ClassificationPipeline(IEnumerable<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>> processors, ILogger<ClassificationPipeline>? logger = null) 
    : PipelinePhase<IDiscoveredArtifact, SemanticMediaType?>("Classification", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, SemanticMediaType? result, CancellationToken cancellationToken = default)
    {
        item.MediaType = result;
        return Task.CompletedTask;
    }
}