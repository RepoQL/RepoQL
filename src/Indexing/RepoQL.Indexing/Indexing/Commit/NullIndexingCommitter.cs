using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.Commit;

internal sealed class NullIndexingCommitter : IIndexingCommitter
{
    public static NullIndexingCommitter Instance { get; } = new();

    private NullIndexingCommitter()
    {
    }

    public Task<CommitOutcome> CommitAsync(IndexItem item, CancellationToken cancellationToken) => Task.FromResult(CommitOutcome.Committed);

    public Task CommitBatchAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken) => Task.CompletedTask;
}

