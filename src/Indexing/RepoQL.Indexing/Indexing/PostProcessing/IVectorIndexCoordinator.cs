using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.PostProcessing;

public interface IVectorIndexCoordinator
{
    Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken);
    Task ApplyAsync(IndexItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Generates structure embeddings (uri + headline + structure) for a batch of items.
    /// Called during hot path idle to enable immediate semantic search.
    /// </summary>
    Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken);
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
}
