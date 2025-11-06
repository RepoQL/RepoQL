using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Formats.Markdown;

public class MarkdownAnalysisProcessor(
    MarkdownAnalyzer analyzer,
    Func<AnalyzerContext> contextFactory,
    ILogger<MarkdownAnalysisProcessor>? logger = null)
    : IAsyncPipeline<IParsedArtifact, Annotation[]>
{
    public async Task<(Annotation[]? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IParsedArtifact item,
        CallNextPipeline<IParsedArtifact, Annotation[]> next,
        CancellationToken token)
    {
        // Check if this is a markdown document
        if (item.MediaType?.Kind != "markdown.doc")
        {
            return await next(item);
        }

        // Try to get the document model from metadata
        if (!item.TryGetValue("document_model", out var docModelObj) ||
            docModelObj is not DocumentModel documentModel)
        {
            logger?.LogWarning("Markdown file {Uri} missing document_model in metadata", item.Uri);
            return await next(item);
        }

        try
        {
            var context = contextFactory();
            var annotations = new List<Annotation>();

            // Get the document node ID from the records
            var documentNode = item.Records?.Nodes.FirstOrDefault(n => n.Kind == "document");
            if (documentNode == null)
            {
                logger?.LogWarning("Markdown file {Uri} missing document node", item.Uri);
                return await next(item);
            }

            // Run the analyzer
            await foreach (var result in analyzer.AnalyzeAsync(documentModel, context, token))
            {
                var annotation = new Annotation
                {
                    SemanticKey = result.SemanticKey,
                    Kind = result.Kind,
                    Severity = result.Severity.ToString().ToLowerInvariant(),
                    Source = result.Source,
                    RuleId = result.RuleId,
                    Message = result.Message,
                    Data = result.Data,
                    ScopeDocumentId = documentNode.Id,
                    TargetNodeId = result.Target?.NodeId,
                    TargetEdgeId = result.Target?.EdgeId,
                    TargetSpanId = result.Target?.SpanId,
                    TargetUri = result.Target?.TargetUri
                };

                annotations.Add(annotation);
            }

            logger?.LogDebug("Analyzed markdown file {Uri}: {Count} annotations", item.Uri, annotations.Count);

            return (annotations.ToArray(), PipelineResult.Success);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to analyze markdown file {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
