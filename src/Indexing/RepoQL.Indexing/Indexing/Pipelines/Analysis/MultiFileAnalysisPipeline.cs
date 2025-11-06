using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Models;

namespace RepoQL.Indexing.Indexing.Pipelines.Analysis;

public class MultiFileAnalysisPipeline(IEnumerable<IAsyncPipeline<IAnnotatedArtifact, Annotation[]>> processors, ILogger<MultiFileAnalysisPipeline>? logger = null) 
    : PipelinePhase<IAnnotatedArtifact, Annotation[]>("MultiFileAnalysis", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, Annotation[]? result, CancellationToken cancellationToken = default)
    {
        if (result != null)
            item.AnnotationsList.AddRange(result);
        return Task.CompletedTask;
    }
}