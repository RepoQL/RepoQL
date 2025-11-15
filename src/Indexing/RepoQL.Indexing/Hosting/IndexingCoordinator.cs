using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Indexing.Hosting;

/// <summary>
/// High-level façade over <see cref="IndexingEngine"/>. Orchestrates reindex operations,
/// provides pipeline status, and exposes user-facing wait APIs.
/// </summary>
/// <remarks>
/// <para><strong>Architecture Layer: Orchestration</strong></para>
/// <para>
/// Sits between <see cref="RepoqlHost"/> (lifecycle) and <see cref="IndexingEngine"/> (execution).
/// Translates high-level operations (like "reindex all files") into engine enqueue calls.
/// </para>
///
/// <para><strong>Pipeline Status</strong></para>
/// <para>
/// Aggregates engine state into user-facing stages:
/// </para>
/// <list type="bullet">
/// <item><description>Discovery: Classification stage</description></item>
/// <item><description>Parsing: Parsing + Single-file Analysis stages</description></item>
/// <item><description>Analysis: Multi-file Analysis + Index Rebuild stages</description></item>
/// <item><description>Writer: Database writer status</description></item>
/// </list>
///
/// <para><strong>Reindex Scopes</strong></para>
/// <para>
/// Tracks reindex operations with <see cref="ReindexScope"/>. While any reindex is active,
/// <see cref="IsReindexing"/> returns true. This affects catalog behavior (skip digest checks
/// during full reindex for performance).
/// </para>
///
/// <para><strong>Wait Operations</strong></para>
/// <para>
/// <see cref="WaitForPipelineAsync"/> and <see cref="WaitForIdleAsync"/> delegate to engine's
/// <see cref="IndexingEngine.WaitForAsync"/> with appropriate state flags.
/// </para>
/// </remarks>
public sealed class IndexingCoordinator : IIndexingCoordinator
{
    private static readonly CoordinatorPipelineStage[] DefaultStages =
    [
        CoordinatorPipelineStage.Discovery,
        CoordinatorPipelineStage.Parsing,
        CoordinatorPipelineStage.Analysis
    ];

    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly CompositeFileSystem _fileSystem;
    private readonly IndexingEngine _engine;
    private readonly IDatabaseWriter _writer;
    private readonly ILogger<IndexingCoordinator> _logger;
    private int _reindexScopes;

    public IndexingCoordinator(
        CompositeFileSystem fileSystem,
        IndexingEngine engine,
        IDatabaseWriter writer,
        ILogger<IndexingCoordinator>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? NullLogger<IndexingCoordinator>.Instance;
    }

    public bool IsReindexing => Volatile.Read(ref _reindexScopes) > 0;

    public PipelineStatusSnapshot GetPipelineStatus()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var discoverySnapshot = _engine.GetHotPathQueueSnapshot();
        var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();
        var writerStatus = _writer.GetStatus();

        var stages = new List<PipelineStageStatusSnapshot>(capacity: 4)
        {
            new(
                CoordinatorPipelineStage.Discovery,
                _engine.GetActiveCount(IndexingState.ClassificationBusy) > 0,
                discoverySnapshot.Queued,
                _engine.GetActiveCount(IndexingState.ClassificationBusy)),
            new(
                CoordinatorPipelineStage.Parsing,
                (_engine.GetActiveCount(IndexingState.ParsingBusy) + _engine.GetActiveCount(IndexingState.SingleFileAnalysisBusy)) > 0,
                discoverySnapshot.Queued,
                _engine.GetActiveCount(IndexingState.ParsingBusy) + _engine.GetActiveCount(IndexingState.SingleFileAnalysisBusy)),
            new(
                CoordinatorPipelineStage.Analysis,
                (_engine.GetActiveCount(IndexingState.MultiFileAnalysisBusy) + _engine.GetActiveCount(IndexingState.IndexRebuildBusy)) > 0,
                analysisSnapshot.Queued,
                _engine.GetActiveCount(IndexingState.MultiFileAnalysisBusy) + _engine.GetActiveCount(IndexingState.IndexRebuildBusy)),
            new(
                CoordinatorPipelineStage.Writer,
                writerStatus.PendingCount > 0,
                writerStatus.PendingCount,
                writerStatus.PendingCount > 0 ? 1 : 0)
        };

        return new PipelineStatusSnapshot(
            capturedAt,
            stages,
            IsReindexing,
            writerStatus.PendingCount > 0);
    }

    public async Task WaitForPipelineAsync(
        IReadOnlyCollection<CoordinatorPipelineStage> stages,
        bool waitAll,
        CancellationToken cancellationToken)
    {
        var targets = (stages is null || stages.Count == 0) ? DefaultStages : stages;
        var waits = new List<Task>(targets.Count);

        foreach (var stage in targets)
        {
            switch (stage)
            {
                case CoordinatorPipelineStage.Discovery:
                    waits.Add(_engine.WaitForAsync(IndexingState.ClassificationIdle, cancellationToken).AsTask());
                    break;
                case CoordinatorPipelineStage.Parsing:
                    waits.Add(_engine.WaitForAsync(IndexingState.ParsingIdle | IndexingState.SingleFileAnalysisIdle, cancellationToken).AsTask());
                    break;
                case CoordinatorPipelineStage.Analysis:
                    waits.Add(_engine.WaitForAsync(IndexingState.MultiFileAnalysisIdle | IndexingState.IndexRebuildIdle, cancellationToken).AsTask());
                    break;
                case CoordinatorPipelineStage.Writer:
                    waits.Add(WaitForWriterIdleAsync(cancellationToken));
                    break;
            }
        }

        if (waits.Count == 0)
            return;

        if (waitAll)
        {
            await Task.WhenAll(waits).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAny(waits).ConfigureAwait(false);
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken)
        => WaitForPipelineAsync(
            new[]
            {
                CoordinatorPipelineStage.Discovery,
                CoordinatorPipelineStage.Parsing,
                CoordinatorPipelineStage.Analysis,
                CoordinatorPipelineStage.Writer
            },
            waitAll: true,
            cancellationToken);

    public async IAsyncEnumerable<ReindexProgressSnapshot> ReindexAsync(
        ReindexRequestOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var scope = new ReindexScope(this);

        _logger.LogInformation("Reindex started.");

        var preparingTimer = Stopwatch.StartNew();
        var preparingActivity = StartPhaseActivity(CoordinatorReindexPhase.Preparing);
        var preparingCompleted = false;
        try
        {
            var preparingSnapshot = new ReindexProgressSnapshot(CoordinatorReindexPhase.Preparing, 0, 0, preparingTimer.Elapsed);
            LogPhaseProgress(preparingSnapshot);
            yield return preparingSnapshot;
            _logger.LogInformation("Preparing completed [Duration: {Duration:F1}s]", preparingTimer.Elapsed.TotalSeconds);
            preparingCompleted = true;
        }
        finally
        {
            CompletePhaseActivity(preparingActivity, preparingTimer, preparingCompleted);
        }

        var artifacts = new List<EnumeratedArtifact>();
        var enumerateTimer = Stopwatch.StartNew();
        var enumerateActivity = StartPhaseActivity(CoordinatorReindexPhase.Enumerating);
        var enumerateCompleted = false;
        try
        {
            _logger.LogInformation("Enumerating files started.");
            await foreach (var resource in _fileSystem.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!resource.File.Exists)
                    continue;

                if (!_engine.Filter.IncludeFile(resource.Uri))
                    continue;

                if (!_fileSystem.TryResolve(resource.Uri, out var store))
                {
                    _logger.LogWarning("No mount resolved URI {Uri} during reindex enumeration.", resource.Uri);
                    continue;
                }

                artifacts.Add(new EnumeratedArtifact(resource.File, store));

                if (artifacts.Count % 250 == 0)
                {
                    var partialSnapshot = new ReindexProgressSnapshot(
                        CoordinatorReindexPhase.Enumerating,
                        artifacts.Count,
                        artifacts.Count,
                        enumerateTimer.Elapsed);
                    LogPhaseProgress(partialSnapshot);
                    yield return partialSnapshot;
                }
            }

            var enumerateSnapshot = new ReindexProgressSnapshot(
                CoordinatorReindexPhase.Enumerating,
                artifacts.Count,
                artifacts.Count,
                enumerateTimer.Elapsed);
            LogPhaseProgress(enumerateSnapshot);
            yield return enumerateSnapshot;
            _logger.LogInformation("Enumerating completed [Items: {Items:N0} Duration: {Duration:F1}s]", artifacts.Count, enumerateTimer.Elapsed.TotalSeconds);
            enumerateCompleted = true;
        }
        finally
        {
            CompletePhaseActivity(
                enumerateActivity,
                enumerateTimer,
                enumerateCompleted,
                activity => activity?.AddTag("reindex.items_total", artifacts.Count));
        }

        var total = artifacts.Count;
        var epoch = _engine.BeginNewEpoch();
        var queueTimer = Stopwatch.StartNew();
        var queueActivity = StartPhaseActivity(CoordinatorReindexPhase.Queueing, epoch, total);
        var queueCompleted = false;
        var queued = 0;
        try
        {
            _logger.LogInformation("Queueing started [Items: {Items:N0}]", total);
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = new RawArtifact(artifact.File, artifact.Store);
                await _engine.EnqueueItemAsync(raw, IndexItemOptions.Always, cancellationToken).ConfigureAwait(false);
                queued++;

                if (queued % 250 == 0 || queued == total)
                {
                    var queueSnapshot = new ReindexProgressSnapshot(CoordinatorReindexPhase.Queueing, total, queued, queueTimer.Elapsed);
                    LogPhaseProgress(queueSnapshot, queueDepth: Math.Max(total - queued, 0));
                    yield return queueSnapshot;
                }
            }

            _logger.LogInformation("Queueing completed [Items: {Items:N0} Duration: {Duration:F1}s]", total, queueTimer.Elapsed.TotalSeconds);
            queueCompleted = true;
        }
        finally
        {
            CompletePhaseActivity(
                queueActivity,
                queueTimer,
                queueCompleted,
                activity =>
                {
                    activity?.AddTag("reindex.items_processed", queued);
                    activity?.AddTag("reindex.items_total", total);
                });
        }

        await foreach (var progress in TrackHotPathAsync(total, epoch, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackPruningAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackVectorRefreshAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackMultiFileAnalysisAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackIndexRebuildAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await WaitForWriterIdleAsync(cancellationToken).ConfigureAwait(false);
        var completedTimer = Stopwatch.StartNew();
        var completedActivity = StartPhaseActivity(CoordinatorReindexPhase.Completed, epoch, total);
        var completed = false;
        try
        {
            var completedSnapshot = new ReindexProgressSnapshot(
                CoordinatorReindexPhase.Completed,
                total,
                total,
                completedTimer.Elapsed);
            LogPhaseProgress(completedSnapshot);
            yield return completedSnapshot;
            _logger.LogInformation("Reindex completed [Items: {Items:N0}]", total);
            completed = true;
        }
        finally
        {
            CompletePhaseActivity(completedActivity, completedTimer, completed);
        }
    }

    private async Task WaitForWriterIdleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Writer flush started.");
        var timer = Stopwatch.StartNew();
        var result = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Writer flush completed [Operations: {Operations:N0} Duration: {Duration:F1}s]",
            result.OperationsFlushed,
            timer.Elapsed.TotalSeconds);
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackHotPathAsync(
        long total,
        long epoch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var activity = StartPhaseActivity(CoordinatorReindexPhase.HotPath, epoch, total);
        var completed = false;
        long lastProcessed = 0;
        try
        {
            _logger.LogInformation("Hot path processing started [Items: {Items:N0}]", total);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = _engine.GetHotPathQueueSnapshot();
                var queued = snapshot.Queued;
                var processed = Math.Clamp(total - queued, 0, total);
                lastProcessed = processed;
                var progress = new ReindexProgressSnapshot(CoordinatorReindexPhase.HotPath, total, processed, timer.Elapsed);
                LogPhaseProgress(progress, queueDepth: queued, inProgress: snapshot.InProgress);
                yield return progress;

                if (snapshot.Depth <= 0 && (_engine.State & IndexingState.Started) == 0)
                    break;

                await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Hot path processing completed [Processed: {Processed:N0} Duration: {Duration:F1}s]", lastProcessed, timer.Elapsed.TotalSeconds);
            completed = true;
        }
        finally
        {
            CompletePhaseActivity(
                activity,
                timer,
                completed,
                act =>
                {
                    act?.AddTag("reindex.items_processed", lastProcessed);
                    act?.AddTag("reindex.items_total", total);
                });
        }
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackPruningAsync(
        long epoch,
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var activity = StartPhaseActivity(CoordinatorReindexPhase.Pruning, epoch, total);
        var completed = false;
        long lastBatchPruned = 0;
        long totalPruned = 0;
        try
        {
            _logger.LogInformation("Pruning started [Items: {Items:N0}]", total);
            var startSnapshot = new ReindexProgressSnapshot(CoordinatorReindexPhase.Pruning, total, 0, timer.Elapsed);
            LogPhaseProgress(startSnapshot);
            yield return startSnapshot;
            while (!cancellationToken.IsCancellationRequested)
            {
                var hasDispatchStarted =
                    _engine.GetAnalysisQueueSnapshot().Depth > 0 ||
                    _engine.GetActiveCount(IndexingState.MultiFileAnalysisBusy) > 0 ||
                    _engine.GetActiveCount(IndexingState.IndexRebuildBusy) > 0;

                var pendingForEpoch = _engine.HasPendingAnalysis(epoch);

                if (hasDispatchStarted || !pendingForEpoch)
                    break;

                await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
            }
            var stats = _engine.GetPruningStatistics();
            lastBatchPruned = stats.LastBatchPruned;
            totalPruned = stats.TotalPruned;
            var completedSnapshot = new ReindexProgressSnapshot(CoordinatorReindexPhase.Pruning, total, total, timer.Elapsed);
            LogPhaseProgress(completedSnapshot, pruned: stats.LastBatchPruned, totalPruned: stats.TotalPruned);
            yield return completedSnapshot;
            _logger.LogInformation("Pruning completed [Items: {Items:N0} Duration: {Duration:F1}s]", total, timer.Elapsed.TotalSeconds);
            completed = true;
        }
        finally
        {
            CompletePhaseActivity(
                activity,
                timer,
                completed,
                act =>
                {
                    act?.AddTag("reindex.items_total", total);
                    act?.AddTag("reindex.pruned_batch", lastBatchPruned);
                    act?.AddTag("reindex.pruned_total", totalPruned);
                });
        }
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackVectorRefreshAsync(
        long epoch,
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var activity = StartPhaseActivity(CoordinatorReindexPhase.VectorRefresh, epoch, total);
        var completed = false;
        long lastProcessed = 0;
        try
        {
            _logger.LogInformation("Vector refresh started [Items: {Items:N0}]", total);
            while (!cancellationToken.IsCancellationRequested)
            {
                var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();
                var pending = analysisSnapshot.Depth;
                var processed = Math.Clamp(total - pending, 0, total);
                lastProcessed = processed;
                var progress = new ReindexProgressSnapshot(CoordinatorReindexPhase.VectorRefresh, total, processed, timer.Elapsed);
                LogPhaseProgress(progress, queueDepth: pending);
                yield return progress;

                if (pending <= 0)
                    break;

                await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Vector refresh completed [Processed: {Processed:N0} Duration: {Duration:F1}s]",
                lastProcessed,
                timer.Elapsed.TotalSeconds);
            completed = true;
        }
        finally
        {
            CompletePhaseActivity(
                activity,
                timer,
                completed,
                act =>
                {
                    act?.AddTag("reindex.items_processed", lastProcessed);
                    act?.AddTag("reindex.items_total", total);
                });
        }
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackMultiFileAnalysisAsync(
        long epoch,
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var activity = StartPhaseActivity(CoordinatorReindexPhase.MultiFileAnalysis, epoch, total);
        var completed = false;
        long lastProcessed = 0;
        try
        {
            _logger.LogInformation("Multi-file analysis started [Items: {Items:N0}]", total);
            while (!cancellationToken.IsCancellationRequested)
            {
                var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();
                var multiBusy = _engine.GetActiveCount(IndexingState.MultiFileAnalysisBusy);
                var pending = analysisSnapshot.Depth + multiBusy;
                var processed = Math.Clamp(total - pending, 0, total);
                lastProcessed = processed;
                var progress = new ReindexProgressSnapshot(CoordinatorReindexPhase.MultiFileAnalysis, total, processed, timer.Elapsed);
                LogPhaseProgress(progress, queueDepth: pending);
                yield return progress;

                if (pending == 0)
                    break;

                await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Multi-file analysis completed [Processed: {Processed:N0} Duration: {Duration:F1}s]",
                lastProcessed,
                timer.Elapsed.TotalSeconds);
            completed = true;
        }
        finally
        {
            CompletePhaseActivity(
                activity,
                timer,
                completed,
                act =>
                {
                    act?.AddTag("reindex.items_processed", lastProcessed);
                    act?.AddTag("reindex.items_total", total);
                });
        }
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackIndexRebuildAsync(
        long epoch,
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var activity = StartPhaseActivity(CoordinatorReindexPhase.IndexRebuild, epoch, total);
        var completed = false;
        long lastProcessed = 0;
        try
        {
            _logger.LogInformation("Index rebuild started [Items: {Items:N0}]", total);
            while (!cancellationToken.IsCancellationRequested)
            {
                var rebuildBusy = _engine.GetActiveCount(IndexingState.IndexRebuildBusy);
                var processed = Math.Clamp(total - rebuildBusy, 0, total);
                lastProcessed = processed;
                var progress = new ReindexProgressSnapshot(CoordinatorReindexPhase.IndexRebuild, total, processed, timer.Elapsed);
                LogPhaseProgress(progress, queueDepth: rebuildBusy);
                yield return progress;

                if (rebuildBusy == 0)
                    break;

                await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Index rebuild completed [Processed: {Processed:N0} Duration: {Duration:F1}s]",
                lastProcessed,
                timer.Elapsed.TotalSeconds);
            completed = true;
        }
        finally
        {
            CompletePhaseActivity(
                activity,
                timer,
                completed,
                act =>
                {
                    act?.AddTag("reindex.items_processed", lastProcessed);
                    act?.AddTag("reindex.items_total", total);
                });
        }
    }

    private readonly record struct EnumeratedArtifact(IFileInfo File, IVirtualFileSystem Store);

    private void LogPhaseProgress(
        ReindexProgressSnapshot snapshot,
        long? queueDepth = null,
        long? pruned = null,
        long? totalPruned = null,
        long? inProgress = null)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        var total = snapshot.TotalItems;
        var processed = snapshot.ProcessedItems;
        var parts = new List<string>();

        if (total > 0)
        {
            var percent = total > 0 ? (double)processed / total * 100 : 0;
            var remaining = Math.Max(total - processed, 0);
            parts.Add($"Processed {processed:N0}/{total:N0} ({percent:0.0}%)");
            parts.Add($"Remaining {remaining:N0}");
        }
        else
        {
            parts.Add($"Processed {processed:N0}");
        }

        if (queueDepth.HasValue)
        {
            parts.Add($"QueueDepth {queueDepth.Value:N0}");
        }

        if (inProgress.HasValue)
        {
            parts.Add($"InProgress {inProgress.Value:N0}");
        }

        if (pruned.HasValue)
        {
            parts.Add($"Pruned {pruned.Value:N0}");
        }

        if (totalPruned.HasValue)
        {
            parts.Add($"TotalPruned {totalPruned.Value:N0}");
        }

        parts.Add($"Elapsed {snapshot.PhaseElapsed.TotalSeconds:F1}s");

        _logger.LogInformation(
            "{Phase} status -> {Details}",
            snapshot.Phase,
            string.Join(" | ", parts));
    }

    private static Activity? StartPhaseActivity(CoordinatorReindexPhase phase, long? epoch = null, long? total = null)
    {
        var tags = new TagList
        {
            { "reindex.phase", phase.ToString() }
        };

        if (epoch.HasValue)
        {
            tags.Add("index.epoch", epoch.Value);
        }

        if (total.HasValue)
        {
            tags.Add("reindex.items_total", total.Value);
        }

        return IndexingEngine.ActivitySource.StartActivity(ActivityKind.Internal, name: $"Reindex.{phase}", tags: tags);
    }

    private static void CompletePhaseActivity(
        Activity? activity,
        Stopwatch stopwatch,
        bool success,
        Action<Activity?>? configure = null)
    {
        if (activity is null)
            return;

        configure?.Invoke(activity);
        activity.AddTag("reindex.duration_ms", stopwatch.Elapsed.TotalMilliseconds);

        if (success)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error);
        }

        activity.Dispose();
    }

    private sealed class ReindexScope : IDisposable
    {
        private readonly IndexingCoordinator _owner;
        private int _disposed;

        public ReindexScope(IndexingCoordinator owner)
        {
            _owner = owner;
            Interlocked.Increment(ref _owner._reindexScopes);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            Interlocked.Decrement(ref _owner._reindexScopes);
        }
    }
}
