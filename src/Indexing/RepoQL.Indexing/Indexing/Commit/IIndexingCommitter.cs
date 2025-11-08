using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing.Commit;

public interface IIndexingCommitter
{
    Task CommitAsync(IndexItem item, CancellationToken cancellationToken);
}

internal sealed class NullIndexingCommitter : IIndexingCommitter
{
    public static NullIndexingCommitter Instance { get; } = new();

    private NullIndexingCommitter()
    {
    }

    public Task CommitAsync(IndexItem item, CancellationToken cancellationToken) => Task.CompletedTask;
}
