using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Indexing.Indexing.Pipelines.Analysis;

public class SingleFileAnalysisPipeline(IEnumerable<IAsyncPipeline<IParsedArtifact, Annotation[]>> processors, ILogger<SingleFileAnalysisPipeline>? logger = null) 
    : PipelinePhase<IParsedArtifact, Annotation[]>("SingleFileAnalysis", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, Annotation[]? result, CancellationToken cancellationToken = default)
    {
        if (result != null)
            item.AnnotationsList.AddRange(result);
        return Task.CompletedTask;
    }
}