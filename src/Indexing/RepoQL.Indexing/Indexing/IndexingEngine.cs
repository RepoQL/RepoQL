using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Indexing.Indexing;

/// <summary>
/// Configuration options for <see cref="IndexingEngine"/>.
/// </summary>
public class IndexingEngineOptions
{
    /// <summary>
    /// Number of concurrent workers for hot-path processing (Classification → Parsing → Analysis → Commit).
    /// Default: <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int IndexingWorkers { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Maximum capacity of the hot-path work queue. Backpressure applied when full.
    /// Default: 10,000 items.
    /// </summary>
    public int IndexingQueueSize {  get; init; } = 10_000;

    /// <summary>
    /// Number of concurrent workers for idle processing (Multi-file Analysis, Index Rebuild).
    /// Default: <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int AnalysisWorkers { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Maximum capacity of the idle-processing work queue. Larger than hot-path because
    /// multi-file operations can spawn many items per batch.
    /// Default: 100,000 items.
    /// </summary>
    public int AnalysisQueueSize {  get; init; } = 100_000;
}

/// <summary>
/// Core indexing pipeline orchestrator. Transforms repository files into queryable graph database
/// through staged discovery: Classification → Parsing → Analysis → Commit.
/// </summary>
/// <remarks>
/// <para><strong>Architecture Pattern: Flow Object</strong></para>
/// <para>
/// Each file is wrapped in an <see cref="IndexItem"/> that accumulates state through pipeline stages.
/// Unlike functional pipelines with immutable transformations, the same object is mutated by each stage,
/// making the entire journey visible for debugging and testing.
/// </para>
///
/// <para><strong>Epoch-Based Batch Coordination</strong></para>
/// <para>
/// Files enqueued together receive the same epoch number. When the last item in an epoch completes
/// and the hot path is idle, the <see cref="HotPathIdle"/> event fires, triggering batch post-processing
/// (pruning, vector refresh, multi-file analysis).
/// </para>
///
/// <para><strong>Threading Model</strong></para>
/// <list type="bullet">
/// <item><description>Hot path: Concurrent (ProcessorCount workers)</description></item>
/// <item><description>Database writer: Serial (1 worker - DuckDB write safety)</description></item>
/// <item><description>Idle processing: Concurrent (ProcessorCount workers)</description></item>
/// </list>
///
/// <para><strong>State Observability</strong></para>
/// <para>
/// Fine-grained <see cref="IndexingState"/> flags track busy/idle per stage. External systems can
/// wait for specific states via <see cref="WaitForAsync"/>. <see cref="StateChanged"/> event fires
/// on every transition.
/// </para>
///
/// <para><strong>Key Invariants</strong></para>
/// <list type="number">
/// <item><description>Database writer is ALWAYS single-threaded</description></item>
/// <item><description>DocumentCatalog updates ONLY via OnCommitted callbacks</description></item>
/// <item><description>Epochs are monotonically increasing (never reused)</description></item>
/// <item><description>Pruner runs BEFORE vector refresh</description></item>
/// <item><description>Analysis sees ONLY committed graph state</description></item>
/// </list>
///
/// <para>See docs/ARCHITECTURE.md for design rationale and docs/JOURNEY.md for complete file flow example.</para>
/// </remarks>
public partial class IndexingEngine
{
    private const string TelemetrySourceName = "RepoQL.Indexing";
    internal static readonly ActivitySource ActivitySource = new(TelemetrySourceName);
    internal static readonly Meter Meter = new(TelemetrySourceName);
    private const IndexingState BusyMask =
        IndexingState.ClassificationBusy |
        IndexingState.ParsingBusy |
        IndexingState.SingleFileAnalysisBusy |
        IndexingState.MultiFileAnalysisBusy |
        IndexingState.IndexRebuildBusy;
    
    public ClassificationPipeline Classifier { get; }
    public ParsingPipeline Parser { get; }
    public SingleFileAnalysisPipeline SingleFileAnalyzer { get; }
    public MultiFileAnalysisPipeline MultiFileAnalyzer { get; }
    public IndexRebuildPipeline IndexRebuilder { get; }
    private long _totalPrunedCount;
    private long _lastPrunedCount;

    private IndexingEngineOptions Options { get; }
    private ILogger<IndexingEngine> Logger { get; }
    private CancellationTokenSource Shutdown { get; } = new();
    private IDocumentCatalog DocumentCatalog { get; }
    private IIndexingCommitter Committer { get; }
    private readonly object _stateLock = new();
    private TaskCompletionSource<bool> _stateChangedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StageContext _classificationStage;
    private readonly StageContext _parsingStage;
    private readonly StageContext _singleFileStage;
    private readonly StageContext _multiFileStage;
    private readonly StageContext _indexRebuildStage;
    private readonly EpochTracker _epochTracker = new();
    private readonly object _analysisLock = new();
    private readonly Dictionary<long, Queue<IndexItem>> _pendingAnalysis = new();
    private readonly Channel<long> _analysisEpochChannel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _idleProcessingTask;
    private readonly ConcurrentDictionary<long, Activity> _epochActivities = new();
    private readonly Dictionary<IndexingState, int> _activeStageCounts = new();
    private long _lastReleasedEpoch = long.MinValue;
    private IArtifactPruner ArtifactPruner { get; }
    private IVectorIndexCoordinator VectorCoordinator { get; }

    internal bool HasPendingAnalysis(long epoch)
    {
        lock (_analysisLock)
        {
            return _pendingAnalysis.TryGetValue(epoch, out var backlog) && backlog.Count > 0;
        }
    }

    public async Task EnqueueItemAsync(RawArtifact artifact, IndexItemOptions options = IndexItemOptions.Default, CancellationToken cancellationToken = default)
    {
        var indexItem = new IndexItem(artifact, options);
        var epoch = _epochTracker.CurrentEpoch;
        indexItem.SetEpoch(epoch);
        _epochTracker.Increment(epoch);
        try
        {
            await IndexerQueue.EnqueueAsync(indexItem, cancellationToken);
        }
        catch
        {
            _epochTracker.Decrement(indexItem.Epoch);
            throw;
        }
    }

    private WorkQueue<IndexItem> IndexerQueue { get; }

    public IndexingEngine(
        IDatabaseWriter? databaseWriter,
        IUriFilter? filter,
        ClassificationPipeline? classifier = null, 
        ParsingPipeline? parser = null, 
        SingleFileAnalysisPipeline? singleFileAnalyzer = null, 
        MultiFileAnalysisPipeline? multiFileAnalyzer = null, 
        IndexRebuildPipeline? indexRebuilder = null,
        IDocumentCatalog? documentCatalog = null,
        IIndexingCommitter? committer = null,
        IArtifactPruner? artifactPruner = null,
        IVectorIndexCoordinator? vectorCoordinator = null,
        IndexingEngineOptions? options = null,
        ILogger<IndexingEngine>? logger = null)
    {
        Writer =  databaseWriter;
        Filter = filter ?? new RepoGitIgnoreFilter(".");
        Classifier = classifier ?? new ClassificationPipeline( []);
        Parser = parser ?? new ParsingPipeline([]);
        SingleFileAnalyzer = singleFileAnalyzer ?? new SingleFileAnalysisPipeline([]);
        MultiFileAnalyzer = multiFileAnalyzer ??  new MultiFileAnalysisPipeline([]);
        IndexRebuilder = indexRebuilder ?? new IndexRebuildPipeline([]);
        DocumentCatalog = documentCatalog ?? new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        Committer = committer
            ?? (databaseWriter is not null
                ? new IndexingCommitter(databaseWriter, DocumentCatalog)
                : NullIndexingCommitter.Instance);
        ArtifactPruner = artifactPruner ?? NullArtifactPruner.Instance;
        VectorCoordinator = vectorCoordinator ?? NullVectorIndexCoordinator.Instance;
        Options = options ??  new IndexingEngineOptions();
        Logger = logger ?? NullLogger<IndexingEngine>.Instance;
        IndexerQueue = new WorkQueue<IndexItem>(
            "IndexingQueue",
            Options.IndexingQueueSize,
            Options.IndexingWorkers,
            async (item, c) =>
            {
                await IndexItemAsync(item, c);
            }, Shutdown.Token);
        AnalysisQueue = new WorkQueue<IndexItem>(
            "AnalysisQueue",
            Options.AnalysisQueueSize,
            Options.AnalysisWorkers,
            async (item, c) =>
            {
                await AnalyzeItemAsync(item, c);
            }, Shutdown.Token);

        _classificationStage = new StageContext(
            IndexingState.ClassificationBusy,
            IndexingState.ClassificationIdle,
            (item, ct) => Classifier.ProcessItemAsync(item, ct));
        _parsingStage = new StageContext(
            IndexingState.ParsingBusy,
            IndexingState.ParsingIdle,
            (item, ct) => Parser.ProcessItemAsync(item, ct));
        _singleFileStage = new StageContext(
            IndexingState.SingleFileAnalysisBusy,
            IndexingState.SingleFileAnalysisIdle,
            (item, ct) => SingleFileAnalyzer.ProcessItemAsync(item, ct));
        _multiFileStage = new StageContext(
            IndexingState.MultiFileAnalysisBusy,
            IndexingState.MultiFileAnalysisIdle,
            (item, ct) => MultiFileAnalyzer.ProcessItemAsync(item, ct));
        _indexRebuildStage = new StageContext(
            IndexingState.IndexRebuildBusy,
            IndexingState.IndexRebuildIdle,
            (item, ct) => IndexRebuilder.ProcessItemAsync(item, ct));
        HotPathIdle += OnHotPathIdle;
        Shutdown.Token.Register(() => _analysisEpochChannel.Writer.TryComplete());
        _idleProcessingTask = Task.Run(ProcessIdleEpochsAsync);
    }

    public event EventHandler<IndexingStateChangedEventArgs>? StateChanged;
    public event EventHandler<HotPathIdleEventArgs>? HotPathIdle;

    public IUriFilter Filter { get; }

    public IDatabaseWriter? Writer { get; }

    public WorkQueue<IndexItem> AnalysisQueue { get; }

    internal WorkQueueSnapshot GetHotPathQueueSnapshot() => IndexerQueue.CaptureSnapshot();

    internal WorkQueueSnapshot GetAnalysisQueueSnapshot() => AnalysisQueue.CaptureSnapshot();

    internal int GetActiveCount(IndexingState busyFlag)
    {
        lock (_stateLock)
        {
            return _activeStageCounts.GetValueOrDefault(busyFlag, 0);
        }
    }

    public long BeginNewEpoch()
    {
        var epoch = _epochTracker.BeginNewEpoch();
        StartEpochActivity(epoch);
        return epoch;
    }

    internal async Task IndexItemAsync(IndexItem item, CancellationToken cancellationToken = default)
    {
        // ReSharper disable once ExplicitCallerInfoArgument
        using var activity = ActivitySource.StartActivity(ActivityKind.Internal, name: "Index", tags: new TagList
        {
            { "item.name", item.Name },
            { "item.uri", item.Uri.ToString() },
            { "item.media_type", item.MediaType },
            { "item.last_modified", item.LastModified.ToString() },
            { "item.provisional_media_type", item.RawArtifact.ProvisionalMediaType }
        });
        var catalogRegistered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Options.HasFlag(IndexItemOptions.OnlyIfNotExcluded) && !Filter.IncludeFile(item.Uri))
            {
                RecordResult(PipelineResult.Filtered);
                return;
            }
            await DocumentCatalog.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var digestBytes = await item.RawArtifact.Digest.WithCancellation(cancellationToken).ConfigureAwait(false);
            var digestHex = Convert.ToHexString(digestBytes);
            item.DigestHex = digestHex;

            var evaluation = DocumentCatalog.Evaluate(item.Uri, digestHex);
            item.ExistingEntry = evaluation.Existing;
            Activity.Current?.AddTag("index.catalog.decision", evaluation.Decision.ToString());

            if (item.Options.HasFlag(IndexItemOptions.OnlyIfStale) &&
                evaluation.Decision == DocumentCatalogDecision.SkipUpToDate)
            {
                Activity.Current?.AddTag("index.catalog", "skip_up_to_date");
                RecordResult(PipelineResult.Filtered);
                return;
            }

            try
            {
                DocumentCatalog.BeginProcessing(item.Uri, digestHex);
                catalogRegistered = true;

                var result = await ApplyIndexerPipeline(item, cancellationToken);
                RecordResult(result);
                if (result != PipelineResult.Success)
                    return;
                await Committer.CommitAsync(item, cancellationToken).ConfigureAwait(false);
                ScheduleAnalysis(item);
                // NOTE: Once WriteOperation dispatch is in place, hook DocumentCatalog.ApplyUpsert/Delete
                //       through the writer's OnCommitted callback to keep the cache authoritative.
            }
            finally
            {
                if (catalogRegistered)
                    DocumentCatalog.CompleteProcessing(item.Uri);
            }
        }
        catch (OperationCanceledException)
        {
            LogIndexingCancelledForItem(Logger, item.Name);
            return;
        }
        catch (Exception ex)
        {
            LogUriFailedDuringIndexing(Logger, ex, item.Uri);
            return;
        }
        finally
        {
            var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
            if (epochBecameIdle && State == IndexingState.AllIdle)
                HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
        }
        Activity.Current?.AddTag("result", "Success");
    }

    private static void RecordResult(PipelineResult result)
    {
        Activity.Current?.AddTag("index.result", result);
    }

    private void ScheduleAnalysis(IndexItem item)
    {
        if (item.Epoch < 0)
            return;

        lock (_analysisLock)
        {
            if (!_pendingAnalysis.TryGetValue(item.Epoch, out var backlog))
            {
                backlog = new Queue<IndexItem>();
                _pendingAnalysis[item.Epoch] = backlog;
            }

            backlog.Enqueue(item);
        }
    }

    private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args)
    {
        EnqueueIdleEpoch(args.Epoch);
    }

    internal void EnqueueIdleEpoch(long epoch)
    {
        if (!_analysisEpochChannel.Writer.TryWrite(epoch))
        {
            Logger.LogWarning("Failed to enqueue epoch {Epoch} for idle processing.", epoch);
        }
    }

    private void StartEpochActivity(long epoch)
    {
        var activity = ActivitySource.StartActivity(ActivityKind.Internal, name: "Indexer.Epoch", tags: new TagList
        {
            { "index.epoch", epoch }
        });

        if (activity is null)
            return;

        if (!_epochActivities.TryAdd(epoch, activity))
        {
            activity.Dispose();
        }
    }

    private void CompleteEpochActivity(long epoch, bool success, Exception? error = null)
    {
        if (!_epochActivities.TryRemove(epoch, out var activity) || activity is null)
            return;

        if (success)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, error?.Message);
            if (error is not null)
            {
                activity.AddTag("exception.type", error.GetType().FullName);
                activity.AddTag("exception.message", error.Message);
            }
        }

        activity.Dispose();
    }

    private async Task ProcessIdleEpochsAsync()
    {
        var reader = _analysisEpochChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(Shutdown.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var epoch))
                {
                    if (epoch <= Interlocked.Read(ref _lastReleasedEpoch))
                        continue;

                    try
                    {
                        await ReleaseAnalysisAsync(epoch).ConfigureAwait(false);
                        Interlocked.Exchange(ref _lastReleasedEpoch, epoch);
                    }
                    catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Idle post-processing failed for epoch {Epoch}.", epoch);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task ReleaseAnalysisAsync(long epoch)
    {
        Exception? failure = null;
        try
        {
            Queue<IndexItem>? backlog = null;
            lock (_analysisLock)
            {
                _pendingAnalysis.Remove(epoch, out backlog);
            }

            if (backlog is null || backlog.Count == 0)
                return;

            var pendingItems = backlog.ToArray();
            var pruningResult = await ArtifactPruner.PruneAsync(pendingItems, Shutdown.Token).ConfigureAwait(false);
            var prunedCount = pruningResult.DeletedArtifacts.Count;
            Interlocked.Exchange(ref _lastPrunedCount, prunedCount);
            if (prunedCount > 0)
            {
                Interlocked.Add(ref _totalPrunedCount, prunedCount);
            }

            if (pruningResult.DeletedArtifacts.Count > 0)
            {
                await DeleteStaleDocumentsAsync(pruningResult.DeletedArtifacts, Shutdown.Token).ConfigureAwait(false);
                await VectorCoordinator.ApplyDeletesAsync(pruningResult.DeletedArtifacts, Shutdown.Token).ConfigureAwait(false);
            }

            foreach (var item in pendingItems)
            {
                await VectorCoordinator.ApplyAsync(item, Shutdown.Token).ConfigureAwait(false);
                await AnalysisQueue.EnqueueAsync(item, Shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            Logger.LogError(ex, "Failed to dispatch analysis work for epoch {Epoch}", epoch);
        }
        finally
        {
            CompleteEpochActivity(epoch, failure is null, failure);
        }
    }

    private async Task DeleteStaleDocumentsAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count == 0)
            return;

        if (Writer is null)
        {
            Logger.LogWarning("Detected {Count} stale documents but no writer is configured; catalog entries will be cleared only in-memory.", deletedArtifacts.Count);
            foreach (var uri in deletedArtifacts)
            {
                DocumentCatalog.ApplyDelete(uri);
            }
            return;
        }

        foreach (var uri in deletedArtifacts)
        {
            var operation = new WriteOperation
            {
                Id = Guid.NewGuid(),
                Type = WriteOperationType.DeleteDocument,
                Uri = uri,
                ParsedData = Records.Empty,
                ParentContext = Activity.Current?.Context,
                OnCommitted = (_, result) =>
                {
                    if (result.Success)
                    {
                        DocumentCatalog.ApplyDelete(uri);
                    }

                    return Task.CompletedTask;
                }
            };

            var commitResult = await Writer.EnqueueAndWaitAsync(operation, cancellationToken).ConfigureAwait(false);
            if (!commitResult.Success)
            {
                throw commitResult.Error ?? new InvalidOperationException($"Database delete failed for {uri}.");
            }
        }
    }

    internal async Task<PipelineResult> ApplyIndexerPipeline(IndexItem item, CancellationToken cancellationToken)
    {
        var pipelineResult = await _classificationStage.RunAsync(item, cancellationToken, UpdateStateFlags).ConfigureAwait(false);
        if (pipelineResult != PipelineResult.Success)
            return  pipelineResult;
        pipelineResult = await _parsingStage.RunAsync(item, cancellationToken, UpdateStateFlags).ConfigureAwait(false);
        if (pipelineResult != PipelineResult.Success)
            return pipelineResult;
        return await _singleFileStage.RunAsync(item, cancellationToken, UpdateStateFlags).ConfigureAwait(false);
    }

    internal async Task AnalyzeItemAsync(IndexItem item, CancellationToken cancellationToken)
    {
        // ReSharper disable once ExplicitCallerInfoArgument
        using var activity = ActivitySource.StartActivity(ActivityKind.Internal, name: "Analyze", tags: new TagList
        {
            { "item.name", item.Name },
            { "item.uri", item.Uri.ToString() },
            { "item.media_type", item.MediaType },
            { "item.last_modified", item.LastModified.ToString() }
        });
        try
        {
            var multiFileTask = _multiFileStage.RunAsync(item, cancellationToken, UpdateStateFlags);
            var rebuildTask = _indexRebuildStage.RunAsync(item, cancellationToken, UpdateStateFlags);
            // Format-specific multi-file analyzers will be plugged into MultiFileAnalyzer; they remain parallel to rebuild.
            await Task.WhenAll(multiFileTask, rebuildTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogAnalysisCancelledForItem(Logger, item.Name);
            return;
        }
        catch (Exception ex)
        {
            LogUriFailedDuringAnalysis(Logger, ex, item.Uri);
            return;
        }
        Activity.Current?.AddTag("result", "Success");
    }
    
    public IndexingState State { get; private set; } = IndexingState.AllIdle;

    public async ValueTask<bool> WaitForAsync(IndexingState state, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_stateLock)
            {
                if (State.HasFlag(state))
                    return true;

                waitTask = _stateChangedTcs.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    private void UpdateStateFlags(IndexingState busyFlag, IndexingState idleFlag, bool isBusy)
    {
        IndexingState oldState;
        IndexingState newState;
        TaskCompletionSource<bool>? toSignal = null;
        EventHandler<IndexingStateChangedEventArgs>? handler = null;

        lock (_stateLock)
        {
            oldState = State;
            var state = State;
            if (isBusy)
            {
                IncrementActiveCount(busyFlag);
                state &= ~idleFlag;
                state |= busyFlag;
            }
            else
            {
                state &= ~busyFlag;
                state |= idleFlag;
                DecrementActiveCount(busyFlag);
            }

            if ((state & BusyMask) != 0)
            {
                state |= IndexingState.Started;
            }
            else
            {
                state &= ~IndexingState.Started;
            }

            if (state == State)
                return;

            State = state;
            toSignal = _stateChangedTcs;
            _stateChangedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = StateChanged;
            newState = state;
        }

        toSignal?.TrySetResult(true);
        handler?.Invoke(this, new IndexingStateChangedEventArgs(oldState, newState));
    }

    public readonly record struct PruningStatistics(long TotalPruned, long LastBatchPruned);

    public PruningStatistics GetPruningStatistics()
    {
        return new PruningStatistics(
            Interlocked.Read(ref _totalPrunedCount),
            Interlocked.Read(ref _lastPrunedCount));
    }

    private void IncrementActiveCount(IndexingState busyFlag)
    {
        if (!_activeStageCounts.TryGetValue(busyFlag, out var current))
        {
            _activeStageCounts[busyFlag] = 1;
            return;
        }

        _activeStageCounts[busyFlag] = current + 1;
    }

    private void DecrementActiveCount(IndexingState busyFlag)
    {
        if (!_activeStageCounts.TryGetValue(busyFlag, out var current))
            return;

        if (current <= 1)
        {
            _activeStageCounts.Remove(busyFlag);
            return;
        }

        _activeStageCounts[busyFlag] = current - 1;
    }

    private sealed class EpochTracker
    {
        private long _currentEpoch;
        private readonly Dictionary<long, int> _pendingByEpoch = new();
        private readonly object _lock = new();

        public long CurrentEpoch => Interlocked.Read(ref _currentEpoch);

        public long BeginNewEpoch() => Interlocked.Increment(ref _currentEpoch);

        public void Increment(long epoch)
        {
            if (epoch < 0) return;
            lock (_lock)
            {
                _pendingByEpoch.TryGetValue(epoch, out var count);
                _pendingByEpoch[epoch] = count + 1;
            }
        }

        public bool Decrement(long epoch)
        {
            if (epoch < 0) return false;
            lock (_lock)
            {
                if (!_pendingByEpoch.TryGetValue(epoch, out var count))
                    return false;

                if (count <= 1)
                {
                    _pendingByEpoch.Remove(epoch);
                    return true;
                }

                _pendingByEpoch[epoch] = count - 1;
                return false;
            }
        }
    }

    #region Logging
    [LoggerMessage(LogLevel.Warning, "Indexing cancelled for {item}")]
    static partial void LogIndexingCancelledForItem(ILogger<IndexingEngine> logger, string item);

    [LoggerMessage(LogLevel.Error, "{Uri} failed during indexing")]
    static partial void LogUriFailedDuringIndexing(ILogger<IndexingEngine> logger, Exception ex, RepoUri uri);

    [LoggerMessage(LogLevel.Warning, "Analysis cancelled for {item}")]
    static partial void LogAnalysisCancelledForItem(ILogger<IndexingEngine> logger, string item);

    [LoggerMessage(LogLevel.Error, "{Uri} failed during analysis")]
    static partial void LogUriFailedDuringAnalysis(ILogger<IndexingEngine> logger, Exception ex, RepoUri uri);
    #endregion
}
