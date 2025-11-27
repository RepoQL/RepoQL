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
            var discovered = new DiscoveredArtifact
            {
                File = item,
                RepoUri = item.Uri,
                MediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value ?? PlainTextLoader.PlainTextMediaType
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
