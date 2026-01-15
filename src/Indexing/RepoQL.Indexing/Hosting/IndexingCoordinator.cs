using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Git;
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

    /// <summary>
    /// Maximum time to wait for queue to drain when workers are idle with no progress.
    /// Prevents infinite polling if workers never pick up queued items.
    /// </summary>
    private static readonly TimeSpan MaxQueueDrainWait = TimeSpan.FromMinutes(1);
    private readonly CompositeFileSystem _fileSystem;
    private readonly ICompositeFileSystemManager? _mountManager;
    private readonly IndexingEngine _engine;
    private readonly DuckDbDataStore _db;
    private readonly GitHistoryIndexer? _gitIndexer;
    private readonly ILogger<IndexingCoordinator> _logger;
    private int _reindexScopes;
    private int _activeMountIndexing;

    public IndexingCoordinator(
        CompositeFileSystem fileSystem,
        IndexingEngine engine,
        DuckDbDataStore db,
        ILogger<IndexingCoordinator>? logger = null,
        ICompositeFileSystemManager? mountManager = null,
        GitHistoryIndexer? gitIndexer = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? NullLogger<IndexingCoordinator>.Instance;
        _mountManager = mountManager;
        _gitIndexer = gitIndexer;

        // Subscribe to mount changes for automatic indexing of new mounts
        if (_mountManager is not null)
        {
            _mountManager.MountsChanged += OnMountChanged;
        }
    }

    public bool IsReindexing => Volatile.Read(ref _reindexScopes) > 0;

    /// <summary>
    /// Triggers incremental git history indexing in the background.
    /// Waits for the pipeline to become idle, then indexes any new commits.
    /// Safe to call multiple times - will only index commits not yet in the database.
    /// </summary>
    public async Task TriggerIncrementalGitIndexingAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("TriggerIncrementalGitIndexingAsync called");

        if (_gitIndexer is null)
        {
            _logger.LogInformation("Git indexer not configured, skipping incremental git indexing");
            return;
        }

        var repoRoot = RepoLocator.FindRepoRoot();
        if (repoRoot is null)
        {
            _logger.LogInformation("Not in a git repository, skipping incremental git indexing");
            return;
        }

        _logger.LogInformation("Waiting for pipeline to become idle before git indexing...");

        // Wait for the file indexing pipeline to stabilize first
        await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Pipeline idle, starting git indexing for {RepoRoot}", repoRoot);

        // Now index any new git commits
        await _gitIndexer.IndexIncrementalAsync(repoRoot, cancellationToken).ConfigureAwait(false);
    }

    public PipelineStatusSnapshot GetPipelineStatus()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var discoverySnapshot = _engine.GetHotPathQueueSnapshot();
        var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();

        // Stage breakdown:
        // - Discovery: Files being enumerated from disk (mount indexing)
        // - Parsing: Hot path queue (classification → parsing → single-file analysis)
        // - Analysis: Multi-file analysis queue
        // - Writer: Idle processing (embeddings, vector refresh)
        var mountIndexing = Volatile.Read(ref _activeMountIndexing);
        var pendingIdleProcessing = _engine.GetPendingIdleProcessingCount();
        var activeIdleProcessing = _engine.ActiveIdleProcessingCount;

        var hotPathActive =
            _engine.GetActiveCount(IndexingState.ClassificationBusy) +
            _engine.GetActiveCount(IndexingState.ParsingBusy) +
            _engine.GetActiveCount(IndexingState.SingleFileAnalysisBusy);

        var stages = new List<PipelineStageStatusSnapshot>(capacity: 4)
        {
            new(
                CoordinatorPipelineStage.Discovery,
                mountIndexing > 0,
                mountIndexing,
                mountIndexing > 0 ? 1 : 0),  // Show 1 active when enumerating
            new(
                CoordinatorPipelineStage.Parsing,
                hotPathActive > 0,
                discoverySnapshot.Queued,
                hotPathActive),
            new(
                CoordinatorPipelineStage.Analysis,
                (_engine.GetActiveCount(IndexingState.MultiFileAnalysisBusy) + _engine.GetActiveCount(IndexingState.IndexRebuildBusy)) > 0,
                analysisSnapshot.Queued,
                _engine.GetActiveCount(IndexingState.MultiFileAnalysisBusy) + _engine.GetActiveCount(IndexingState.IndexRebuildBusy)),
            new(
                CoordinatorPipelineStage.Writer,
                activeIdleProcessing > 0,
                pendingIdleProcessing,
                activeIdleProcessing)
        };

        return new PipelineStatusSnapshot(
            capturedAt,
            stages,
            IsReindexing,
            false);  // WriterPending = false (sync writes)
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
            waits.Add(WaitForStageCompleteAsync(stage, cancellationToken));
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

    /// <summary>
    /// Waits for a pipeline stage to complete, considering both worker state and queue depth.
    /// </summary>
    /// <remarks>
    /// A stage is considered complete when:
    /// 1. All workers for that stage (and upstream stages) are idle
    /// 2. The work queue is empty (no pending items)
    ///
    /// This prevents returning prematurely when items are queued but workers haven't started yet.
    /// Times out after <see cref="MaxQueueDrainWait"/> to prevent infinite polling when workers
    /// are idle but queue never drains.
    /// </remarks>
    private async Task WaitForStageCompleteAsync(CoordinatorPipelineStage stage, CancellationToken cancellationToken)
    {
        var requiredState = GetRequiredIdleState(stage);
        var stuckTimer = Stopwatch.StartNew();
        var lastQueueDepth = long.MaxValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Wait for state flags to indicate idle
            await _engine.WaitForAsync(requiredState, cancellationToken).ConfigureAwait(false);

            // After state indicates idle, verify queue is also empty
            // This handles the race between enqueue and worker pickup
            var currentDepth = GetQueueDepthForStage(stage);

            // Success: workers idle and queue empty
            if (currentDepth == 0)
            {
                return;
            }

            // Queue still has items but workers are idle
            // If queue depth decreased, reset the stuck timer (progress being made)
            if (currentDepth < lastQueueDepth)
            {
                stuckTimer.Restart();
                lastQueueDepth = currentDepth;
            }
            // If idle processing is active (e.g., embedding), work is happening even if depth doesn't change.
            // Only applies to stages that include idle processing in their depth (Parsing, Analysis, Writer).
            else if (stage != CoordinatorPipelineStage.Discovery && _engine.ActiveIdleProcessingCount > 0)
            {
                // Reset timer - embedding or other idle processing is actively running
                stuckTimer.Restart();
            }
            else if (stuckTimer.Elapsed > MaxQueueDrainWait)
            {
                // No progress for MaxQueueDrainWait - timeout to prevent infinite wait
                _logger.LogWarning(
                    "WaitForPipelineAsync timed out waiting for {Stage} queue to drain. " +
                    "Workers are idle but queue depth={Depth} after {Elapsed:F1}s with no progress. " +
                    "This may indicate workers are not processing queued items.",
                    stage, currentDepth, stuckTimer.Elapsed.TotalSeconds);
                return;
            }

            // Workers should pick up items and become busy again
            // Wait a short interval before rechecking to avoid tight polling
            await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private long GetQueueDepthForStage(CoordinatorPipelineStage stage)
    {
        var hotPathSnapshot = _engine.GetHotPathQueueSnapshot();
        var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();
        var mountIndexing = Volatile.Read(ref _activeMountIndexing);

        return stage switch
        {
            // All stages must wait for mount indexing to complete (files are being enumerated)
            CoordinatorPipelineStage.Discovery => hotPathSnapshot.Depth + mountIndexing,
            // Parsing stage must wait for idle processing which generates structure embeddings.
            // Structure embeddings are created in ReleaseAnalysisAsync after hot path completes.
            CoordinatorPipelineStage.Parsing => hotPathSnapshot.Depth + mountIndexing + _engine.GetPendingIdleProcessingCount(),
            CoordinatorPipelineStage.Analysis => hotPathSnapshot.Depth + analysisSnapshot.Depth + mountIndexing + _engine.GetPendingIdleProcessingCount(),
            // Writer stage (SemanticIndexing) must wait for idle post-processing which includes
            // vector/embedding refresh. Items in _pendingAnalysis haven't yet been processed
            // through ReleaseAnalysisAsync which triggers VectorCoordinator.ApplyAsync().
            CoordinatorPipelineStage.Writer => hotPathSnapshot.Depth + analysisSnapshot.Depth + mountIndexing + _engine.GetPendingIdleProcessingCount(),
            _ => 0
        };
    }

    private static IndexingState GetRequiredIdleState(CoordinatorPipelineStage stage)
    {
        // Each stage must wait for all upstream stages to complete.
        // Pipeline order: Discovery -> Parsing -> Analysis -> Writer
        return stage switch
        {
            CoordinatorPipelineStage.Discovery => IndexingState.ClassificationIdle,
            CoordinatorPipelineStage.Parsing =>
                IndexingState.ClassificationIdle |
                IndexingState.ParsingIdle |
                IndexingState.SingleFileAnalysisIdle,
            CoordinatorPipelineStage.Analysis =>
                IndexingState.ClassificationIdle |
                IndexingState.ParsingIdle |
                IndexingState.SingleFileAnalysisIdle |
                IndexingState.MultiFileAnalysisIdle |
                IndexingState.IndexRebuildIdle,
            CoordinatorPipelineStage.Writer =>
                IndexingState.ClassificationIdle |
                IndexingState.ParsingIdle |
                IndexingState.SingleFileAnalysisIdle |
                IndexingState.MultiFileAnalysisIdle |
                IndexingState.IndexRebuildIdle,
            _ => IndexingState.ClassificationIdle
        };
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
        _db.TryCheckpoint(); // Checkpoint after hot path to persist indexed artifacts

        await foreach (var progress in TrackPruningAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
        _db.TryCheckpoint(); // Checkpoint after pruning

        await foreach (var progress in TrackVectorRefreshAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
        _db.TryCheckpoint(); // Checkpoint after structure embeddings

        await foreach (var progress in TrackMultiFileAnalysisAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
        _db.TryCheckpoint(); // Checkpoint after analysis

        await foreach (var progress in TrackIndexRebuildAsync(epoch, total, cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
        _db.TryCheckpoint(); // Checkpoint after full embeddings

        await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);

        // Index git history after file indexing completes
        if (_gitIndexer is not null)
        {
            var repoRoot = RepoLocator.FindRepoRoot();
            if (repoRoot is not null)
            {
                _logger.LogInformation("Indexing git history...");
                await _gitIndexer.IndexAsync(repoRoot, cancellationToken).ConfigureAwait(false);
            }
        }

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

    /// <summary>
    /// Handles mount addition/update events by automatically indexing files from the new mount.
    /// </summary>
    private void OnMountChanged(object? sender, CompositeFileSystemMountChangedEventArgs e)
    {
        // Only process Added or Updated mounts (skip Removed for now)
        if (e.Kind != MountChangeKind.Added && e.Kind != MountChangeKind.Updated)
            return;

        // Skip primary mounts - they're handled by the initial scan in RepoqlHost
        if (e.Mount.IsPrimary)
            return;

        _logger.LogInformation(
            "Mount {Kind}: {MountId} (scheme={Scheme}) - starting indexing",
            e.Kind,
            e.Mount.Id,
            e.Mount.FileSystem.Scheme);

        // Track that mount indexing is in progress (for WaitForPipelineAsync)
        Interlocked.Increment(ref _activeMountIndexing);

        // Index the mount in the background to avoid blocking the caller
        _ = Task.Run(async () =>
        {
            try
            {
                await IndexMountAsync(e.Mount, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMountIndexing);
            }
        });
    }

    /// <summary>
    /// Enumerates and indexes all files from a specific mount using incremental indexing.
    /// </summary>
    private async Task IndexMountAsync(CompositeFileSystemMount mount, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var enqueued = 0;
        var skipped = 0;

        try
        {
            _logger.LogInformation("Enumerating files from mount {MountId}", mount.Id);

            // Enumerate directly from the mount's filesystem
            await foreach (var file in mount.FileSystem.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!file.Exists)
                {
                    skipped++;
                    continue;
                }

                var uri = mount.FileSystem.GetUri(file);

                // Check if the file should be indexed (respects filters like .gitignore)
                if (!_engine.Filter.IncludeFile(uri))
                {
                    skipped++;
                    continue;
                }

                // Enqueue with Default options for incremental indexing
                // (OnlyIfStale | OnlyIfNotExcluded) - will skip unchanged files
                var artifact = new RawArtifact(file, mount.FileSystem);
                await _engine.EnqueueItemAsync(artifact, IndexItemOptions.Default, cancellationToken).ConfigureAwait(false);
                enqueued++;

                if (enqueued % 100 == 0)
                {
                    _logger.LogDebug("Mount {MountId}: enqueued {Count} files so far", mount.Id, enqueued);
                }
            }

            _logger.LogInformation(
                "Mount {MountId} enumeration completed: {Enqueued} files enqueued, {Skipped} skipped in {Duration:F1}s. Waiting for hot path...",
                mount.Id,
                enqueued,
                skipped,
                sw.Elapsed.TotalSeconds);

            // Wait for hot path and idle processing (structure embeddings) to complete
            // This ensures WaitForPipelineAsync returns only after all files are fully indexed
            await _engine.WaitForAsync(
                IndexingState.ClassificationIdle | IndexingState.ParsingIdle | IndexingState.SingleFileAnalysisIdle,
                cancellationToken).ConfigureAwait(false);

            // Also wait for pending idle processing (structure embeddings) to drain
            while (_engine.GetPendingIdleProcessingCount() > 0)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            // No flush needed - sync writes mean structure embeddings are already written

            sw.Stop();
            _logger.LogInformation(
                "Mount {MountId} indexing fully completed in {Duration:F1}s",
                mount.Id,
                sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Mount {MountId} indexing was cancelled after {Duration:F1}s", mount.Id, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index mount {MountId} after {Duration:F1}s", mount.Id, sw.Elapsed.TotalSeconds);
        }
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
