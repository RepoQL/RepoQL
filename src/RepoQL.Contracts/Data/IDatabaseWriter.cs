using System.Diagnostics;
using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Data;

public interface IDatabaseWriter : IAsyncDisposable
{
    ValueTask EnqueueAsync(WriteOperation operation, CancellationToken ct = default);
    ValueTask<CommitResult> EnqueueAndWaitAsync(WriteOperation operation, CancellationToken ct = default);
    Task<FlushResult> FlushAsync(CancellationToken ct = default);
    WriterStatus GetStatus();
}

public sealed record WriteOperation
{
    public required Guid Id { get; init; }
    public required WriteOperationType Type { get; init; }
    public required RepoUri Uri { get; init; }
    public required Records ParsedData { get; init; }
    /// <summary>
    ///     Optional parent activity context so DB write participates in the same distributed trace.
    /// </summary>
    public ActivityContext? ParentContext { get; init; }
    public Func<WriteOperation, CommitResult, Task>? OnCommitted { get; init; }
}

public enum WriteOperationType
{
    ReplaceDocument,
    Barrier
}

public sealed record CommitResult
{
    public bool Success { get; init; }
    public Exception? Error { get; init; }
}

public sealed record FlushResult
{
    public int OperationsFlushed { get; init; }
}

public sealed record WriterStatus
{
    public int PendingCount { get; init; }
    public long TotalProcessed { get; init; }
}
