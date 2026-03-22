using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Csv;

/// <summary>
/// Pipeline processor for CSV/TSV/PSV files.
///
/// Purpose: Integrates <see cref="CsvLoader"/> into the parsing pipeline so
/// classified delimited artifacts are converted into graph records.
///
/// Complexity: Minimal orchestration wrapper around loader execution and
/// pipeline error handling.
/// </summary>
public sealed class CsvParser(CsvLoader loader, ILogger<CsvParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly CsvLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILogger<CsvParser> _logger = logger ?? NullLogger<CsvParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!IsCsvKind(item.MediaType?.Kind))
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
                "Parsed delimited file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri,
                records.Nodes.Length,
                records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse delimited file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }

    private static bool IsCsvKind(string? kind)
    {
        return string.Equals(kind, "csv.table", StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, "tsv.table", StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, "data.psv", StringComparison.OrdinalIgnoreCase);
    }
}
