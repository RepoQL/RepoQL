using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.PostProcessing;

public interface IEmbeddingCoordinator
{
    Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken);
    Task ApplyAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken);
    Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken);
}

public sealed class NullEmbeddingCoordinator : IEmbeddingCoordinator
{
    public static IEmbeddingCoordinator Instance { get; } = new NullEmbeddingCoordinator();

    private NullEmbeddingCoordinator()
    {
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ApplyAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
