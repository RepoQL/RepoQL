using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Indexing;

/// <summary>
/// Processor delegate for a pipeline stage.
/// </summary>
internal delegate Task<PipelineResult> StageProcessor(IndexItem item, CancellationToken cancellationToken);

/// <summary>
/// Wraps a pipeline stage with automatic state management. Ensures busy/idle flags
/// are always set and cleared correctly, even when processor throws.
/// </summary>
/// <remarks>
/// <para><strong>Automatic State Transitions</strong></para>
/// <para>
/// When <see cref="StageContextExtensions.RunAsync"/> is called:
/// </para>
/// <list type="number">
/// <item><description>Sets busy flag, clears idle flag</description></item>
/// <item><description>Calls processor</description></item>
/// <item><description>Clears busy flag, sets idle flag (always, even on error)</description></item>
/// </list>
///
/// <para><strong>Usage Pattern</strong></para>
/// <code>
/// var stage = new StageContext(
///     IndexingState.ParsingBusy,
///     IndexingState.ParsingIdle,
///     (item, ct) => parser.ProcessItemAsync(item, ct)
/// );
///
/// var result = await stage.RunAsync(item, ct, UpdateState);
/// </code>
///
/// <para>
/// This pattern eliminates manual state management bugs and provides consistent telemetry.
/// </para>
/// </remarks>
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
            var result = await stage.Processor(item, cancellationToken).ConfigureAwait(false);
            if (result == PipelineResult.Cancelled && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            return result;
        }
        finally
        {
            updateState(stage.BusyFlag, stage.IdleFlag, false);
        }
    }
}
