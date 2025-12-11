using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.Commit;

public interface IIndexingCommitter
{
    Task CommitAsync(IndexItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Commit multiple items in a single batch transaction for better performance.
    /// </summary>
    Task CommitBatchAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken);
}

internal sealed class NullIndexingCommitter : IIndexingCommitter
{
    public static NullIndexingCommitter Instance { get; } = new();

    private NullIndexingCommitter()
    {
    }

    public Task CommitAsync(IndexItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CommitBatchAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken) => Task.CompletedTask;
}
