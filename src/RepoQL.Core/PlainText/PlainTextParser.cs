using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Core.PlainText;

internal sealed class PlainTextParser(PlainTextLoader loader, ILogger<PlainTextParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly PlainTextLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILogger<PlainTextParser> _logger = logger ?? NullLogger<PlainTextParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!ShouldProcess(item))
        {
            return await next(item).ConfigureAwait(false);
        }

        try
        {
            // Ensure the item has a media type - this is critical for the committer.
            // If no classifier assigned one and there's no provisional type from the extension,
            // we assign a fallback so the file is indexed rather than skipped entirely.
            var resolvedMediaType = item.MediaType
                ?? item.RawArtifact.ProvisionalMediaType.Value
                ?? PlainTextLoader.PlainTextMediaType;

            // Set the media type on the item so it propagates to the committer
            if (item is IndexItem indexItem && indexItem.MediaType is null)
            {
                indexItem.MediaType = resolvedMediaType;
                _logger.LogWarning("Assigned fallback media type {MediaType} to {Uri}", resolvedMediaType, item.Uri);
            }

            var discovered = new DiscoveredArtifact
            {
                File = item,
                RepoUri = item.Uri,
                MediaType = resolvedMediaType
            };

            var documentModel = await _loader.LoadAsync(discovered, token).ConfigureAwait(false);
            item["document_model"] = documentModel;

            var records = _loader.Materialize(documentModel);
            _logger.LogTrace("Plain text fallback parsed {Uri} -> {NodeCount} nodes", item.Uri, records.Nodes.Length);
            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse plain text file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }

    /// <summary>
    /// Fallback parser accepts everything - this ensures all files are indexed for discoverability.
    /// </summary>
    private static bool ShouldProcess(IClassifiedArtifact artifact) => true;
}
