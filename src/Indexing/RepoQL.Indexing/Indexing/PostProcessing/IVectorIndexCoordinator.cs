using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.PostProcessing;

public interface IVectorIndexCoordinator
{
    Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken);
    Task ApplyAsync(IndexItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Generates structure embeddings (uri + headline + structure) for a batch of items.
    /// Called by eager post-commit embedding workers; idle processing waits on completion before analysis dispatch.
    /// </summary>
    Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes VSS (Vector Similarity Search) HNSW indexes for fast approximate nearest neighbor search.
    /// Called during idle processing after embeddings are generated.
    /// </summary>
    Task RefreshVssIndexAsync(CancellationToken cancellationToken);
}

public sealed class NullVectorIndexCoordinator : IVectorIndexCoordinator
{
    public static IVectorIndexCoordinator Instance { get; } = new NullVectorIndexCoordinator();

    private NullVectorIndexCoordinator()
    {
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task ApplyAsync(IndexItem item, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task RefreshVssIndexAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
