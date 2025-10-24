namespace RepoQL.Contracts.Data;

public interface IDatabaseWriter : IAsyncDisposable
{
    ValueTask EnqueueAsync(WriteOperation operation, CancellationToken ct = default);
    ValueTask<CommitResult> EnqueueAndWaitAsync(WriteOperation operation, CancellationToken ct = default);
    Task<FlushResult> FlushAsync(CancellationToken ct = default);
    WriterStatus GetStatus();
}