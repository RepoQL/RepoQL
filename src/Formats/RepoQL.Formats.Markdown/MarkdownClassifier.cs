using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Markdown;

public class MarkdownClassifier(ILogger<MarkdownClassifier>? logger = null)
    : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        // Check if this is a markdown file
        var extension = Path.GetExtension(item.Name).ToLowerInvariant();

        // Not a markdown file, pass to next processor
        if (extension is not (".md" or ".markdown")) 
            return next(item);
        
        logger?.LogDebug("Classified {Uri} as markdown.doc", item.Uri);
        var mediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
        return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
    }
}
