using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Pdf;

/// <summary>
/// Classifier for PDF files.
///
/// Purpose: Recognizes .pdf files without opening them and assigns the base
/// application/pdf media type.
///
/// Complexity: None - simple extension check only.
/// </summary>
public sealed class PdfClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((PdfMediaTypes.Base, PipelineResult.Success));
        }

        return next(item);
    }
}
