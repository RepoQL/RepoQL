using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Ruby;

/// <summary>
/// Classifies Ruby source artifacts into Ruby semantic kinds.
///
/// Purpose: Route Ruby files to the Ruby parser with stable kind metadata.
///
/// Complexity: Extension and filename mapping only.
/// </summary>
public sealed class RubyClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        var extension = Path.GetExtension(item.Name);
        if (RubyMediaTypes.IsErb(extension))
        {
            return next(item);
        }

        if (RubyMediaTypes.TryResolve(item.Name, out var mediaType))
        {
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
        }

        return next(item);
    }
}
