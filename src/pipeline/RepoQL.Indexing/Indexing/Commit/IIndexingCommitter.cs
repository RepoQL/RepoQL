using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.Commit;

public interface IIndexingCommitter
{
    Task<CommitOutcome> CommitAsync(IndexItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Commit multiple items in a single batch transaction for better performance.
    /// </summary>
    Task CommitBatchAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken);
}

