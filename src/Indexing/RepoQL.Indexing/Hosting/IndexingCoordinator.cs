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
        var preparingSnapshot = new ReindexProgressSnapshot(CoordinatorReindexPhase.Preparing, 0, 0, preparingTimer.Elapsed);
        LogPhaseProgress(preparingSnapshot);
        yield return preparingSnapshot;
        _logger.LogInformation("Preparing completed [Duration: {Duration:F1}s]", preparingTimer.Elapsed.TotalSeconds);

        var artifacts = new List<EnumeratedArtifact>();
        var enumerateTimer = Stopwatch.StartNew();
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

        var total = artifacts.Count;
        var queueTimer = Stopwatch.StartNew();
        var queued = 0;
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

        await foreach (var progress in TrackHotPathAsync(total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackPruningAsync(total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackVectorRefreshAsync(total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackMultiFileAnalysisAsync(total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await foreach (var progress in TrackIndexRebuildAsync(total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await WaitForWriterIdleAsync(cancellationToken).ConfigureAwait(false);
        var completedSnapshot = new ReindexProgressSnapshot(
            CoordinatorReindexPhase.Completed,
            total,
            total,
            Stopwatch.StartNew().Elapsed);
        LogPhaseProgress(completedSnapshot);
        yield return completedSnapshot;
        _logger.LogInformation("Reindex completed [Items: {Items:N0}]", total);
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        _logger.LogInformation("Hot path processing started [Items: {Items:N0}]", total);
        long lastProcessed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _engine.GetHotPathQueueSnapshot();
            var processed = Math.Clamp(total - snapshot.Depth, 0, total);
            lastProcessed = processed;
            var progress = new ReindexProgressSnapshot(CoordinatorReindexPhase.HotPath, total, processed, timer.Elapsed);
            LogPhaseProgress(progress, queueDepth: snapshot.Depth);
            yield return progress;

            if (snapshot.Depth <= 0 && (_engine.State & IndexingState.Started) == 0)
                break;

            await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Hot path processing completed [Processed: {Processed:N0} Duration: {Duration:F1}s]", lastProcessed, timer.Elapsed.TotalSeconds);
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackPruningAsync(
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
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

            if (hasDispatchStarted)
                break;

            await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
        }
        var stats = _engine.GetPruningStatistics();
        var completedSnapshot = new ReindexProgressSnapshot(CoordinatorReindexPhase.Pruning, total, total, timer.Elapsed);
        LogPhaseProgress(completedSnapshot, pruned: stats.LastBatchPruned, totalPruned: stats.TotalPruned);
        yield return completedSnapshot;
        _logger.LogInformation("Pruning completed [Items: {Items:N0} Duration: {Duration:F1}s]", total, timer.Elapsed.TotalSeconds);
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackVectorRefreshAsync(
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        _logger.LogInformation("Vector refresh started [Items: {Items:N0}]", total);
        long lastProcessed = 0;
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
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackMultiFileAnalysisAsync(
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        _logger.LogInformation("Multi-file analysis started [Items: {Items:N0}]", total);
        long lastProcessed = 0;
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
    }

    private async IAsyncEnumerable<ReindexProgressSnapshot> TrackIndexRebuildAsync(
        long total,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        _logger.LogInformation("Index rebuild started [Items: {Items:N0}]", total);
        long lastProcessed = 0;
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
    }

    private readonly record struct EnumeratedArtifact(IFileInfo File, IVirtualFileSystem Store);

    private void LogPhaseProgress(
        ReindexProgressSnapshot snapshot,
        long? queueDepth = null,
        long? pruned = null,
        long? totalPruned = null)
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
