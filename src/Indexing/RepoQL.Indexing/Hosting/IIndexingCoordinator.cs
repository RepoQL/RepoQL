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
}
