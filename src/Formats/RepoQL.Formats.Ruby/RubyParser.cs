using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Ruby;

/// <summary>
/// Parsing pipeline adapter for Ruby artifacts.
///
/// Purpose: Execute Ruby loading/materialization for classified Ruby kinds.
///
/// Complexity: Thin orchestration with pipeline error handling.
/// </summary>
public sealed class RubyParser(RubyLoader loader, ILogger<RubyParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly RubyLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILogger<RubyParser> _logger = logger ?? NullLogger<RubyParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!RubyMediaTypes.IsSupportedKind(item.MediaType?.Kind))
        {
            return await next(item).ConfigureAwait(false);
        }

        try
        {
            if (item is not IFileInfo fileInfo)
                throw new InvalidOperationException("Item must implement IFileInfo");

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
                "Parsed Ruby file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri,
                records.Nodes.Length,
                records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Ruby file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
