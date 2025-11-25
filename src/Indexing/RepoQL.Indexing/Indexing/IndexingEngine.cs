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
using RepoQL.Metrics;

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
public partial class IndexingEngine : IAsyncDisposable
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
    private IndexingMetrics? Metrics { get; }
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
    private readonly Dictionary<IndexingState, StageCounter> _stageCounters = new();
    private bool _disposed;
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
        await EnqueueIndexItemAsync(indexItem, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<bool> EnqueueIndexItemAsync(IndexItem indexItem, CancellationToken cancellationToken = default)
    {
        var epoch = _epochTracker.CurrentEpoch;
        indexItem.SetEpoch(epoch);

        var incremented = false;
        try
        {
            var enqueued = await IndexerQueue.EnqueueAsync(indexItem, cancellationToken).ConfigureAwait(false);
            if (!enqueued)
                return false;

            _epochTracker.Increment(epoch);
            incremented = true;
            return true;
        }
        catch
        {
            if (incremented)
            {
                _epochTracker.Decrement(indexItem.Epoch);
            }
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
        ILogger<IndexingEngine>? logger = null,
        IndexingMetrics? metrics = null)
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
        Metrics = metrics;
        IndexerQueue = new WorkQueue<IndexItem>(
            "IndexingQueue",
            Options.IndexingQueueSize,
            Options.IndexingWorkers,
            async (item, c) =>
            {
                await IndexItemAsync(item, c);
            }, Shutdown.Token, meter: null, comparer: new IndexItemComparer());
        AnalysisQueue = new WorkQueue<IndexItem>(
            "AnalysisQueue",
            Options.AnalysisQueueSize,
            Options.AnalysisWorkers,
            async (item, c) =>
            {
                await AnalyzeItemAsync(item, c);
            }, Shutdown.Token, meter: null, comparer: new IndexItemComparer());

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

        RegisterStageCounter(IndexingState.ClassificationBusy, IndexingState.ClassificationIdle);
        RegisterStageCounter(IndexingState.ParsingBusy, IndexingState.ParsingIdle);
        RegisterStageCounter(IndexingState.SingleFileAnalysisBusy, IndexingState.SingleFileAnalysisIdle);
        RegisterStageCounter(IndexingState.MultiFileAnalysisBusy, IndexingState.MultiFileAnalysisIdle);
        RegisterStageCounter(IndexingState.IndexRebuildBusy, IndexingState.IndexRebuildIdle);
        lock (_stateLock)
        {
            State = ComputeStateFromCounters();
        }

        // Register observable gauge callbacks for metrics
        RegisterMetricsCallbacks();
    }

    /// <summary>
    /// Registers observable gauge callbacks with the metrics system.
    /// </summary>
    private void RegisterMetricsCallbacks()
    {
        if (Metrics is null)
            return;

        // Register queue callbacks
        Metrics.RegisterQueueCallbacks(
            indexerDepth: () => IndexerQueue.Depth,
            analysisDepth: () => AnalysisQueue.Depth,
            writerDepth: () => Writer?.GetStatus().PendingCount ?? 0,
            indexerCapacity: () => IndexerQueue.MaxDepth,
            analysisCapacity: () => AnalysisQueue.MaxDepth,
            writerCapacity: () => Writer?.QueueCapacity ?? 0,
            indexerWorkers: () => GetActiveCount(IndexingState.ClassificationBusy) +
                                  GetActiveCount(IndexingState.ParsingBusy) +
                                  GetActiveCount(IndexingState.SingleFileAnalysisBusy),
            analysisWorkers: () => GetActiveCount(IndexingState.MultiFileAnalysisBusy) +
                                   GetActiveCount(IndexingState.IndexRebuildBusy)
        );

        // Register catalog callbacks
        Metrics.RegisterCatalogCallbacks(
            entryCount: () => DocumentCatalog.EntryCount,
            pendingCount: () => DocumentCatalog.PendingDigestCount
        );

        // Register epoch callbacks
        Metrics.RegisterEpochCallbacks(
            currentEpoch: () => _epochTracker.CurrentEpoch,
            pendingItems: () => _epochTracker.CurrentPendingItems
        );
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
            return _stageCounters.TryGetValue(busyFlag, out var counter)
                ? counter.ActiveCount
                : 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            Shutdown.Cancel();
        }
        catch { }

        try
        {
            _analysisEpochChannel.Writer.TryComplete();
        }
        catch { }

        try
        {
            await IndexerQueue.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        try
        {
            await AnalysisQueue.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        if (_idleProcessingTask is not null)
        {
            try
            {
                await _idleProcessingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        Shutdown.Dispose();
    }

    public long BeginNewEpoch()
    {
        var epoch = _epochTracker.BeginNewEpoch();
        StartEpochActivity(epoch);
        return epoch;
    }

    internal async Task IndexItemAsync(IndexItem item, CancellationToken cancellationToken = default)
    {
        var overallTimer = Stopwatch.StartNew();
        var status = "unknown";
        var currentStage = "enqueue";
        var mime = item.MediaType?.ToString()
                   ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString()
                   ?? "unknown";
        var fileSize = item.RawArtifact.Length;
        var catalogRegistered = false;

        // Record file enqueued
        Metrics?.FilesEnqueued.Add(1, new TagList
        {
            { "mime_type", mime },
            { "read_only", item.IsReadOnly.ToString().ToLowerInvariant() }
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentStage = "filter";
            if (item.Options.HasFlag(IndexItemOptions.OnlyIfNotExcluded) && !Filter.IncludeFile(item.Uri))
            {
                RecordResult(item.Epoch, PipelineResult.Filtered);
                status = "filtered";
                Metrics?.FilesFiltered.Add(1, new TagList
                {
                    { "reason", "gitignore" },
                    { "mime_type", mime }
                });
                return;
            }

            currentStage = "catalog_init";
            await DocumentCatalog.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var digestBytes = await item.RawArtifact.Digest.WithCancellation(cancellationToken).ConfigureAwait(false);
            var digestHex = Convert.ToHexString(digestBytes);
            item.DigestHex = digestHex;

            var evaluation = DocumentCatalog.Evaluate(item.Uri, digestHex);
            item.ExistingEntry = evaluation.Existing;
            AddEpochTag(item.Epoch, "index.catalog.decision", evaluation.Decision.ToString());

            if (item.Options.HasFlag(IndexItemOptions.OnlyIfStale) &&
                evaluation.Decision == DocumentCatalogDecision.SkipUpToDate)
            {
                AddEpochTag(item.Epoch, "index.catalog", "skip_up_to_date");
                RecordResult(item.Epoch, PipelineResult.Filtered);
                status = "skipped_up_to_date";
                Metrics?.FilesSkipped.Add(1, new TagList
                {
                    { "reason", "up_to_date" },
                    { "mime_type", mime }
                });
                return;
            }

            try
            {
                DocumentCatalog.BeginProcessing(item.Uri, digestHex);
                catalogRegistered = true;

                currentStage = "pipeline";
                var result = await ApplyIndexerPipeline(item, cancellationToken);
                mime = item.MediaType?.ToString() ?? mime;
                status = result.ToString();
                RecordResult(item.Epoch, result);
                if (result != PipelineResult.Success)
                    return;

                currentStage = "commit";
                var commitTimer = Stopwatch.StartNew();
                await Committer.CommitAsync(item, cancellationToken).ConfigureAwait(false);
                commitTimer.Stop();
                RecordStageDuration("commit", commitTimer.Elapsed.TotalMilliseconds, PipelineResult.Success, item);

                // Record successful indexing
                Metrics?.FilesIndexed.Add(1, new TagList
                {
                    { "mime_type", mime },
                    { "status", "success" }
                });

                status = "success";
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
            status = "cancelled";
            LogIndexingCancelledForItem(Logger, item.Name);
            return;
        }
        catch (Exception ex)
        {
            status = "error";
            Metrics?.FilesErrored.Add(1, new TagList
            {
                { "mime_type", mime },
                { "error_type", TruncateErrorType(ex.GetType().Name) },
                { "stage", currentStage }
            });
            LogUriFailedDuringIndexing(Logger, ex, item.Uri);
            return;
        }
        finally
        {
            overallTimer.Stop();
            Metrics?.HotPathDuration.Record(overallTimer.Elapsed.TotalMilliseconds, new TagList
            {
                { "status", status },
                { "mime_type", mime },
                { "read_only", item.IsReadOnly.ToString().ToLowerInvariant() }
            });
            Metrics?.RecordFileProcessed(mime, status, fileSize, overallTimer.Elapsed.TotalMilliseconds);
            AddEpochTag(item.Epoch, "index.result", status);
            var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
            if (epochBecameIdle && State == IndexingState.AllIdle)
                HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
        }
    }

    /// <summary>
    /// Truncates error type names to known categories to limit metric cardinality.
    /// </summary>
    private static string TruncateErrorType(string errorType)
    {
        var knownErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IOException", "UnauthorizedAccessException", "FileNotFoundException",
            "DirectoryNotFoundException", "DuckDBException", "ArgumentException",
            "ArgumentNullException", "InvalidOperationException", "TimeoutException",
            "OperationCanceledException", "TaskCanceledException", "OutOfMemoryException",
            "NullReferenceException", "IndexOutOfRangeException", "FormatException",
            "JsonException", "XmlException", "NotSupportedException", "NotImplementedException",
            "ObjectDisposedException"
        };
        return knownErrors.Contains(errorType) ? errorType : "other";
    }

    private void RecordResult(long epoch, PipelineResult result)
    {
        AddEpochTag(epoch, "index.pipeline_result", result.ToString());
    }

    private void RecordStageDuration(string stage, double durationMs, PipelineResult result, IndexItem item)
    {
        Metrics?.StageDuration.Record(durationMs, new TagList
        {
            { "stage", stage },
            { "status", result.ToString() },
            { "mime", item.MediaType?.ToString() ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString() ?? "unknown" },
            { "read_only", item.IsReadOnly }
        });
    }

    private void ScheduleAnalysis(IndexItem item)
    {
        if (item.IsReadOnly)
        {
            AddEpochTag(item.Epoch, "analysis.skip", "read_only_idle");
            return;
        }
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
        Metrics?.IdleCycles.Add(1);
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

    private void AddEpochTag(long epoch, string key, object? value)
    {
        if (epoch < 0)
            return;

        if (_epochActivities.TryGetValue(epoch, out var activity) && activity is not null)
        {
            activity.AddTag(key, value);
        }
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
        var epochTimer = Stopwatch.StartNew();
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

            // Prune phase
            var pruneTimer = Stopwatch.StartNew();
            var pruningResult = await ArtifactPruner.PruneAsync(pendingItems, Shutdown.Token).ConfigureAwait(false);
            pruneTimer.Stop();
            var prunedCount = pruningResult.DeletedArtifacts.Count;
            Interlocked.Exchange(ref _lastPrunedCount, prunedCount);
            if (prunedCount > 0)
            {
                Interlocked.Add(ref _totalPrunedCount, prunedCount);
                Metrics?.FilesPruned.Add(prunedCount);
            }
            Metrics?.IdlePhaseDuration.Record(pruneTimer.Elapsed.TotalMilliseconds, new TagList
            {
                { "phase", "prune" }
            });

            if (pruningResult.DeletedArtifacts.Count > 0)
            {
                await DeleteStaleDocumentsAsync(pruningResult.DeletedArtifacts, Shutdown.Token).ConfigureAwait(false);
                await VectorCoordinator.ApplyDeletesAsync(pruningResult.DeletedArtifacts, Shutdown.Token).ConfigureAwait(false);
            }

            // Vector refresh phase
            var vectorTimer = Stopwatch.StartNew();
            foreach (var item in pendingItems)
            {
                await VectorCoordinator.ApplyAsync(item, Shutdown.Token).ConfigureAwait(false);
            }
            vectorTimer.Stop();
            Metrics?.IdlePhaseDuration.Record(vectorTimer.Elapsed.TotalMilliseconds, new TagList
            {
                { "phase", "vector_refresh" }
            });

            // Multi-file analysis enqueue phase
            var analysisEnqueueTimer = Stopwatch.StartNew();
            foreach (var item in pendingItems)
            {
                await AnalysisQueue.EnqueueAsync(item, Shutdown.Token).ConfigureAwait(false);
            }
            analysisEnqueueTimer.Stop();
            Metrics?.IdlePhaseDuration.Record(analysisEnqueueTimer.Elapsed.TotalMilliseconds, new TagList
            {
                { "phase", "multi_file_analysis" }
            });
        }
        catch (Exception ex)
        {
            failure = ex;
            Logger.LogError(ex, "Failed to dispatch analysis work for epoch {Epoch}", epoch);
        }
        finally
        {
            epochTimer.Stop();

            // Record epoch metrics
            var epochSize = _epochTracker.GetEpochTotalItems(epoch);
            if (epochSize > 0)
            {
                Metrics?.EpochSize.Record(epochSize);
                Metrics?.EpochDuration.Record(epochTimer.Elapsed.TotalMilliseconds);
                Metrics?.EpochsCompleted.Add(1);
            }

            // Clear peak tracking to prevent memory leaks
            _epochTracker.ClearEpochPeak(epoch);

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
        var mime = item.MediaType?.ToString()
                   ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString()
                   ?? "unknown";

        // Classification stage
        var classifyTimer = Stopwatch.StartNew();
        var pipelineResult = await _classificationStage.RunAsync(item, cancellationToken, UpdateStateFlags).ConfigureAwait(false);
        classifyTimer.Stop();
        mime = item.MediaType?.ToString() ?? mime; // Update mime after classification
        RecordStageDuration("classification", classifyTimer.Elapsed.TotalMilliseconds, pipelineResult, item);
        Metrics?.FilesClassified.Add(1, new TagList
        {
            { "mime_type", mime },
            { "result", pipelineResult.ToString() }
        });
        if (pipelineResult != PipelineResult.Success)
            return pipelineResult;

        // Parsing stage
        var parseTimer = Stopwatch.StartNew();
        pipelineResult = await _parsingStage.RunAsync(item, cancellationToken, UpdateStateFlags).ConfigureAwait(false);
        parseTimer.Stop();
        RecordStageDuration("parsing", parseTimer.Elapsed.TotalMilliseconds, pipelineResult, item);
        Metrics?.FilesParsed.Add(1, new TagList
        {
            { "mime_type", mime },
            { "result", pipelineResult.ToString() }
        });
        if (pipelineResult != PipelineResult.Success)
            return pipelineResult;

        if (item.IsReadOnly)
        {
            AddEpochTag(item.Epoch, "analysis.skip", "read_only_single");
            return PipelineResult.Success;
        }

        // Single-file analysis stage
        var analysisTimer = Stopwatch.StartNew();
        pipelineResult = await _singleFileStage.RunAsync(item, cancellationToken, UpdateStateFlags).ConfigureAwait(false);
        analysisTimer.Stop();
        RecordStageDuration("single_file_analysis", analysisTimer.Elapsed.TotalMilliseconds, pipelineResult, item);
        Metrics?.FilesEnriched.Add(1, new TagList
        {
            { "mime_type", mime },
            { "result", pipelineResult.ToString() }
        });

        return pipelineResult;
    }

    internal async Task AnalyzeItemAsync(IndexItem item, CancellationToken cancellationToken)
    {
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
        AddEpochTag(item.Epoch, "analysis.result", "Success");
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
            if (!_stageCounters.TryGetValue(busyFlag, out var counter))
            {
                counter = new StageCounter(busyFlag, idleFlag);
                _stageCounters[busyFlag] = counter;
            }

            counter.ActiveCount += isBusy ? 1 : -1;
            if (counter.ActiveCount < 0)
            {
                counter.ActiveCount = 0;
            }

            var state = ComputeStateFromCounters();
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

    private void RegisterStageCounter(IndexingState busyFlag, IndexingState idleFlag)
    {
        if (_stageCounters.ContainsKey(busyFlag))
            return;
        _stageCounters[busyFlag] = new StageCounter(busyFlag, idleFlag);
    }

    private IndexingState ComputeStateFromCounters()
    {
        IndexingState state = 0;
        if (_stageCounters.Count == 0)
        {
            return IndexingState.AllIdle;
        }

        foreach (var counter in _stageCounters.Values)
        {
            if (counter.ActiveCount > 0)
            {
                state |= counter.BusyFlag;
            }
            else
            {
                state |= counter.IdleFlag;
            }
        }

        if (state == 0)
        {
            state = IndexingState.AllIdle;
        }

        if ((state & BusyMask) != 0)
        {
            state |= IndexingState.Started;
        }
        else
        {
            state &= ~IndexingState.Started;
        }

        return state;
    }

    private sealed class StageCounter
    {
        public StageCounter(IndexingState busyFlag, IndexingState idleFlag)
        {
            BusyFlag = busyFlag;
            IdleFlag = idleFlag;
        }

        public IndexingState BusyFlag { get; }
        public IndexingState IdleFlag { get; }
        public int ActiveCount;
    }

    private sealed class IndexItemComparer : IEqualityComparer<IndexItem>
    {
        public bool Equals(IndexItem? x, IndexItem? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;
            return x.Uri == y.Uri && x.Options == y.Options;
        }

        public int GetHashCode(IndexItem obj)
        {
            if (obj is null)
                return 0;
            return HashCode.Combine(obj.Uri, obj.Options);
        }
    }

    private sealed class EpochTracker
    {
        private long _currentEpoch;
        private readonly Dictionary<long, int> _pendingByEpoch = new();
        private readonly Dictionary<long, int> _peakByEpoch = new();
        private readonly object _lock = new();

        public long CurrentEpoch => Interlocked.Read(ref _currentEpoch);

        /// <summary>
        /// Gets the number of pending items in the current epoch.
        /// </summary>
        public int CurrentPendingItems
        {
            get
            {
                lock (_lock)
                {
                    var epoch = Interlocked.Read(ref _currentEpoch);
                    return _pendingByEpoch.TryGetValue(epoch, out var count) ? count : 0;
                }
            }
        }

        public long BeginNewEpoch() => Interlocked.Increment(ref _currentEpoch);

        public void Increment(long epoch)
        {
            if (epoch < 0) return;
            lock (_lock)
            {
                _pendingByEpoch.TryGetValue(epoch, out var count);
                var newCount = count + 1;
                _pendingByEpoch[epoch] = newCount;

                // Track peak for this epoch
                _peakByEpoch.TryGetValue(epoch, out var peak);
                if (newCount > peak)
                {
                    _peakByEpoch[epoch] = newCount;
                }
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

        /// <summary>
        /// Gets the total items that were processed in an epoch (peak count).
        /// </summary>
        public int GetEpochTotalItems(long epoch)
        {
            lock (_lock)
            {
                return _peakByEpoch.TryGetValue(epoch, out var peak) ? peak : 0;
            }
        }

        /// <summary>
        /// Clears peak tracking for completed epochs to prevent memory leaks.
        /// Call after epoch metrics are recorded.
        /// </summary>
        public void ClearEpochPeak(long epoch)
        {
            lock (_lock)
            {
                _peakByEpoch.Remove(epoch);
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
