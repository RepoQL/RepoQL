using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Hosting;

/// <summary>
/// Default <see cref="IIndexingWorkScheduler"/> implementation that delegates to <see cref="Indexing.Indexing.IndexingEngine"/>.
/// </summary>
public sealed class IndexingEngineWorkScheduler(IndexingEngine engine) : IIndexingWorkScheduler
{
    private readonly IndexingEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public Task EnqueueAsync(RawArtifact artifact, IndexItemOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return _engine.EnqueueItemAsync(artifact, options, cancellationToken);
    }
}
