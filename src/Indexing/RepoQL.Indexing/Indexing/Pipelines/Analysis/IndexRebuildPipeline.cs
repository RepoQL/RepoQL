using Microsoft.Extensions.Logging;

namespace RepoQL.Indexing.Indexing.Pipelines.Analysis;

public class IndexRebuildPipeline(IEnumerable<IAsyncPipeline<IAnnotatedArtifact, string>> processors, ILogger<IndexRebuildPipeline>? logger = null) 
    : PipelinePhase<IAnnotatedArtifact, string>("IndexRebuild", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, string result, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}