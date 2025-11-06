using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Markdown;

public class MarkdownParser(MarkdownLoader loader, ILogger<MarkdownParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        // Check if this is a markdown document
        if (item.MediaType?.Kind != "markdown.doc")
        {
            return await next(item);
        }

        try
        {
            // Create a DiscoveredArtifact to pass to the loader
            var discoveredArtifact = new DiscoveredArtifact
            {
                File = item as IFileInfo ?? throw new InvalidOperationException("Item must implement IFileInfo"),
                RepoUri = item.Uri,
                MediaType = item.MediaType
            };

            // Load the document
            var documentModel = await loader.LoadAsync(discoveredArtifact, token);

            // Store the document model in metadata for the analyzer
            item["document_model"] = documentModel;

            // Materialize into records
            var records = loader.Materialize(documentModel);

            logger?.LogDebug("Parsed markdown file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri, records.Nodes.Length, records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to parse markdown file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
