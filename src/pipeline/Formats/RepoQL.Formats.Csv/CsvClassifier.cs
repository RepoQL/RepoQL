using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Csv;

/// <summary>
/// Classifier for CSV/TSV/PSV files.
///
/// Purpose: Identifies delimited text artifacts early in indexing and assigns
/// a stable semantic media kind for downstream parsing.
///
/// Complexity: None. The classifier is an extension-based routing step.
/// </summary>
public sealed class CsvClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    private static readonly SemanticMediaType CsvMediaType =
        SemanticMediaType.Create("text", "csv").WithKind("csv.table");

    private static readonly SemanticMediaType TsvMediaType =
        SemanticMediaType.Create("text", "tab-separated-values").WithKind("tsv.table");

    private static readonly SemanticMediaType PsvMediaType =
        SemanticMediaType.Create("text", "plain").WithKind("data.psv");

    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (item.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((CsvMediaType, PipelineResult.Success));

        if (item.Name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((TsvMediaType, PipelineResult.Success));

        if (item.Name.EndsWith(".psv", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((PsvMediaType, PipelineResult.Success));

        return next(item);
    }
}
