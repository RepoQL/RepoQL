using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Indexing.Indexing.Pipelines.Parsing;

public class ParsingPipeline(IEnumerable<IAsyncPipeline<IClassifiedArtifact, Records?>> processors, ILogger<ParsingPipeline>? logger = null) 
    : PipelinePhase<IClassifiedArtifact, Records?>("Parsing", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, Records? result, CancellationToken cancellationToken = default)
    {
        item.Records = result;
        return Task.CompletedTask;
    }
}