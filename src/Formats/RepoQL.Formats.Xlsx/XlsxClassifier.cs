using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Xlsx;

/// <summary>
/// Classifier for XLSX files.
///
/// Purpose: Identifies XLSX files and sets the correct media type with kind
/// during the classification pipeline stage.
///
/// Complexity: None - simple extension check.
/// </summary>
public sealed class XlsxClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    private static readonly SemanticMediaType XlsxMediaType =
        SemanticMediaType.Create("application", "xlsx")
            .WithKind("xlsx.workbook");

    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        var name = item.Name;
        if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((XlsxMediaType, PipelineResult.Success));
        }

        return next(item);
    }
}
