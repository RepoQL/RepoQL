using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Json;

/// <summary>
/// Pipeline processor for JSON files.
///
/// Purpose: Integrates <see cref="JsonLoader"/> into parsing so classified JSON artifacts become graph records.
///
/// Complexity: Minimal orchestration wrapper around load/materialize and pipeline error handling.
/// </summary>
public sealed class JsonParser(JsonLoader loader, ILogger<JsonParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly JsonLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILogger<JsonParser> _logger = logger ?? NullLogger<JsonParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!IsJsonKind(item.MediaType?.Kind))
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

            if (!await _loader.CanLoadAsync(discovered, token).ConfigureAwait(false))
                return await next(item).ConfigureAwait(false);

            var documentModel = await _loader.LoadAsync(discovered, token).ConfigureAwait(false);
            item["document_model"] = documentModel;

            var records = _loader.Materialize(documentModel);

            _logger.LogDebug(
                "Parsed JSON file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri,
                records.Nodes.Length,
                records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JSON file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }

    private static bool IsJsonKind(string? kind)
        => kind?.StartsWith("json", StringComparison.OrdinalIgnoreCase) == true;
}
