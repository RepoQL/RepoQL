using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing;

internal delegate Task<PipelineResult> StageProcessor(IndexItem item, CancellationToken cancellationToken);

internal readonly struct StageContext
{
    public StageContext(IndexingState busyFlag, IndexingState idleFlag, StageProcessor processor)
    {
        BusyFlag = busyFlag;
        IdleFlag = idleFlag;
        Processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public IndexingState BusyFlag { get; }
    public IndexingState IdleFlag { get; }
    public StageProcessor Processor { get; }
}

internal static class StageContextExtensions
{
    public static async Task<PipelineResult> RunAsync(
        this StageContext stage,
        IndexItem item,
        CancellationToken cancellationToken,
        Action<IndexingState, IndexingState, bool> updateState)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(updateState);

        updateState(stage.BusyFlag, stage.IdleFlag, true);
        try
        {
            return await stage.Processor(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            updateState(stage.BusyFlag, stage.IdleFlag, false);
        }
    }
}
