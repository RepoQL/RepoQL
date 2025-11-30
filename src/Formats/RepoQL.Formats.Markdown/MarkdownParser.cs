using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Markdown;

public sealed class MarkdownParser(MarkdownLoader loader, ILogger<MarkdownParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly MarkdownLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILogger<MarkdownParser> _logger = logger ?? NullLogger<MarkdownParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (item.MediaType?.Kind != "markdown.doc")
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
                "Parsed markdown file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri,
                records.Nodes.Length,
                records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse markdown file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
