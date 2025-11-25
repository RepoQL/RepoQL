using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.TypeScript;

public sealed class TypeScriptParser(TypeScriptLoader loader, ILogger<TypeScriptParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (item.MediaType?.Kind is not ("code.typescript" or "code.typescript.react" or "code.javascript" or "code.javascript.react"))
        {
            return await next(item);
        }

        try
        {
            var discoveredArtifact = new DiscoveredArtifact
            {
                File = item as IFileInfo ?? throw new InvalidOperationException("Item must implement IFileInfo"),
                RepoUri = item.Uri,
                MediaType = item.MediaType
            };

            var documentModel = await loader.LoadAsync(discoveredArtifact, token).ConfigureAwait(false);
            item["document_model"] = documentModel;

            var records = loader.Materialize(documentModel);

            logger?.LogDebug("Parsed TypeScript/JavaScript file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri, records.Nodes.Length, records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to parse TypeScript/JavaScript file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
