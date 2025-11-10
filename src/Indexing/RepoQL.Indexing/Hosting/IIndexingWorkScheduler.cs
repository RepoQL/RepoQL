using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Hosting;

/// <summary>
/// Abstraction for components capable of accepting <see cref="RawArtifact"/> instances for indexing.
/// </summary>
public interface IIndexingWorkScheduler
{
    Task EnqueueAsync(RawArtifact artifact, IndexItemOptions options, CancellationToken cancellationToken);
}
