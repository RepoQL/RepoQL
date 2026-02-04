using RepoQL.Contracts;

namespace RepoQL.Indexing.Hosting;

public sealed record PipelineStageStatusSnapshot(
    CoordinatorPipelineStage Stage,
    bool Busy,
    int Queued,
    int InProgress);

public sealed record PipelineStatusSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<PipelineStageStatusSnapshot> Stages,
    bool IsReindexing,
    bool WriterPending);

public sealed record ReindexProgressSnapshot(
    CoordinatorReindexPhase Phase,
    long TotalItems,
    long ProcessedItems,
    TimeSpan PhaseElapsed);

public sealed record ReindexRequestOptions(bool Clear);

/// <summary>
/// Reindex operation handle that streams progress and exposes the created operation.
/// <para><b>Purpose:</b> Allow callers to track the underlying operation while iterating progress.</para>
/// <para><b>Complexity:</b> Combines async enumeration with an operation task.</para>
/// </summary>
public interface IReindexOperation : IAsyncEnumerable<ReindexProgressSnapshot>
{
    Task<IOperation?> Operation { get; }
}

public enum CoordinatorPipelineStage
{
    Discovery,
    Parsing,
    Analysis,
    Writer
}

public enum CoordinatorReindexPhase
{
    Preparing,
    Enumerating,
    Queueing,
    HotPath,
    Pruning,
    VectorRefresh,
    MultiFileAnalysis,
    IndexRebuild,
    Completed
}

public interface IIndexingCoordinator
{
    bool IsReindexing { get; }

    PipelineStatusSnapshot GetPipelineStatus();

    Task WaitForPipelineAsync(
        IReadOnlyCollection<CoordinatorPipelineStage> stages,
        bool waitAll,
        CancellationToken cancellationToken);

    Task WaitForIdleAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ReindexProgressSnapshot> ReindexAsync(
        ReindexRequestOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Triggers incremental git history indexing in the background.
    /// Waits for the pipeline to become idle, then indexes any new commits.
    /// </summary>
    Task TriggerIncrementalGitIndexingAsync(CancellationToken cancellationToken = default);
}
