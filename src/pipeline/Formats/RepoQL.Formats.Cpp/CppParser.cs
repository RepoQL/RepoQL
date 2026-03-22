using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Cpp;

/// <summary>
/// Parsing pipeline adapter for C/C++ artifacts.
///
/// Purpose: Execute C/C++ loading/materialization for classified C/C++ kinds.
///
/// Complexity: Thin orchestration with pipeline error handling.
/// </summary>
public sealed class CppParser(CppMaterializer materializer, ILogger<CppParser>? logger = null)
    : IAsyncPipeline<IClassifiedArtifact, Records?>
{
    private readonly CppMaterializer _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    private readonly ILogger<CppParser> _logger = logger ?? NullLogger<CppParser>.Instance;

    public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IClassifiedArtifact item,
        CallNextPipeline<IClassifiedArtifact, Records?> next,
        CancellationToken token)
    {
        if (!CppMediaTypes.IsSupportedKind(item.MediaType?.Kind))
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

            var documentModel = await _materializer.LoadAsync(discovered, token).ConfigureAwait(false);
            item["document_model"] = documentModel;

            var records = _materializer.Materialize(documentModel);
            _logger.LogDebug(
                "Parsed C/C++ file {Uri}: {NodeCount} nodes, {SpanCount} spans",
                item.Uri,
                records.Nodes.Length,
                records.Spans.Length);

            return (records, PipelineResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse C/C++ file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
