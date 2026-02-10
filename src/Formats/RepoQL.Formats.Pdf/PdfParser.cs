using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Pdf;

/// <summary>
/// Pipeline processor for parsing PDF files.
///
/// Purpose: Integrates PdfLoader into the indexing parsing stage.
///
/// Complexity: Minimal orchestration only.
/// </summary>
public sealed class PdfParser(PdfLoader loader, ILogger<PdfParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly PdfLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILogger<PdfParser> _logger = logger ?? NullLogger<PdfParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!PdfMediaTypes.IsPdf(item.MediaType))
            return await next(item).ConfigureAwait(false);

        try
        {
            if (item is not IFileInfo fileInfo)
                throw new InvalidOperationException("Item must implement IFileInfo.");

            var discovered = new DiscoveredArtifact
            {
                File = fileInfo,
                RepoUri = item.Uri,
                MediaType = item.MediaType
            };

            var documentModel = await _loader.LoadAsync(discovered, token).ConfigureAwait(false);
            item["document_model"] = documentModel;

            var records = _loader.Materialize(documentModel);

            _logger.LogDebug(
                "Parsed PDF file {Uri}: {NodeCount} nodes",
                item.Uri,
                records.Nodes.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse PDF file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
