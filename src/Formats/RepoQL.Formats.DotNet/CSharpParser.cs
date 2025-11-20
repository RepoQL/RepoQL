using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.DotNet;

public class CSharpParser(CSharpLoader loader, ILogger<CSharpParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!string.Equals(item.MediaType?.Kind, CSharpLoader.MediaKind, StringComparison.OrdinalIgnoreCase))
        {
            return await next(item);
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

            var documentModel = await loader.LoadAsync(discovered, token);
            item["document_model"] = documentModel;

            var records = loader.Materialize(documentModel);

            logger?.LogDebug(
                "Parsed C# file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri,
                records.Nodes.Length,
                records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to parse C# file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
