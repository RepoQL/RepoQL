using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
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
    public int IndexingWorkers { get; init; } = Environment.ProcessorCount * 2;

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

    /// <summary>
    /// Maximum time allowed for processing a single item in the hot-path queue.
    /// If an item exceeds this duration, it is considered timed out and skipped.
    /// This prevents stuck items from blocking the entire pipeline (FM-001 mitigation).
    /// Default: 5 minutes (sufficient for most Roslyn compilations, TypeScript parsing, etc.).
    /// Set to null to disable per-item timeout (not recommended).
    /// </summary>
    public TimeSpan? HotPathItemTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time allowed for processing a single item in the analysis queue.
    /// Analysis items typically involve multi-file operations and may take longer.
    /// Default: 10 minutes. Set to null to disable timeout.
    /// </summary>
    public TimeSpan? AnalysisItemTimeout { get; init; } = TimeSpan.FromMinutes(10);
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
    private readonly Dictionary<long, Queue<IndexItem>> _pendingStructureEmbeddings = new();  // Separate from analysis - includes read-only items
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
    private int _activeIdleProcessingCount;
    private string? _lastError;
    private IArtifactPruner ArtifactPruner { get; }
    internal IVectorIndexCoordinator VectorCoordinator { get; }

    // Diagnostic accessors (used by IndexingEngineDiagnosticsProvider)
    internal int ActiveIdleProcessingCount => Volatile.Read(ref _activeIdleProcessingCount);
    internal string? LastError => Volatile.Read(ref _lastError);
    internal long CurrentEpoch => _epochTracker.CurrentEpoch;
    internal int HotPathTimeoutCount => IndexerQueue.TimeoutCount;
    internal int AnalysisTimeoutCount => AnalysisQueue.TimeoutCount;

    /// <summary>
    /// Gets items currently being processed in the hot-path queue with their durations.
    /// Useful for diagnosing potentially stuck items.
    /// </summary>
    internal IReadOnlyList<(IndexItem Item, TimeSpan Duration)> GetHotPathInFlightItems()
        => IndexerQueue.GetInFlightItems();

    /// <summary>
    /// Gets items currently being processed in the analysis queue with their durations.
    /// </summary>
    internal IReadOnlyList<(IndexItem Item, TimeSpan Duration)> GetAnalysisInFlightItems()
        => AnalysisQueue.GetInFlightItems();

    internal bool HasPendingAnalysis(long epoch)
    {
        lock (_analysisLock)
        {
            return _pendingAnalysis.TryGetValue(epoch, out var backlog) && backlog.Count > 0;
        }
    }

    /// <summary>
    /// Returns a non-zero value if there is pending idle post-processing work (including semantic indexing).
    /// This includes:
    /// <list type="bullet">
    /// <item>Items in <c>_pendingAnalysis</c> waiting for <c>HotPathIdle</c> to dispatch them</item>
    /// <item>Items in <c>_pendingStructureEmbeddings</c> waiting for structure embedding generation (includes read-only imports)</item>
    /// <item>Epochs currently being processed in <c>ReleaseAnalysisAsync</c> (pruning, vector refresh, analysis enqueue)</item>
    /// </list>
    /// </summary>
    internal int GetPendingIdleProcessingCount()
    {
        lock (_analysisLock)
        {
            var count = Volatile.Read(ref _activeIdleProcessingCount);
            foreach (var backlog in _pendingAnalysis.Values)
            {
                count += backlog.Count;
            }
            // Also count structure embeddings - these include read-only imports that skip _pendingAnalysis
            foreach (var backlog in _pendingStructureEmbeddings.Values)
            {
                count += backlog.Count;
            }
            return count;
        }
    }

    /// <summary>
    /// Gets items currently in the hot path queue (for diagnostics).
    /// </summary>
    internal IReadOnlyList<IndexItem> GetHotPathPendingItems() => IndexerQueue.GetPendingItems();

    /// <summary>
    /// Gets items waiting for idle processing (for diagnostics).
    /// </summary>
    internal IReadOnlyList<IndexItem> GetPendingAnalysisItems()
    {
        lock (_analysisLock)
        {
            return _pendingAnalysis.Values.SelectMany(q => q).ToList();
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
        DuckDbDataStore? db,
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
        Database = db;
        Filter = filter ?? new RepoGitIgnoreFilter(".");
        Classifier = classifier ?? new ClassificationPipeline( []);
        Parser = parser ?? new ParsingPipeline([]);
        SingleFileAnalyzer = singleFileAnalyzer ?? new SingleFileAnalysisPipeline([]);
        MultiFileAnalyzer = multiFileAnalyzer ??  new MultiFileAnalysisPipeline([]);
        IndexRebuilder = indexRebuilder ?? new IndexRebuildPipeline([]);
        DocumentCatalog = documentCatalog ?? new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        Committer = committer ?? NullIndexingCommitter.Instance;
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
            },
            Shutdown.Token,
            itemTimeout: Options.HotPathItemTimeout,
            meter: null,
            comparer: new IndexItemComparer(),
            logger: Logger)
        {
            OnItemTimeout = HandleHotPathItemTimeout
        };
        AnalysisQueue = new WorkQueue<IndexItem>(
            "AnalysisQueue",
            Options.AnalysisQueueSize,
            Options.AnalysisWorkers,
            async (item, c) =>
            {
                await AnalyzeItemAsync(item, c);
            },
            Shutdown.Token,
            itemTimeout: Options.AnalysisItemTimeout,
            meter: null,
            comparer: new IndexItemComparer(),
            logger: Logger);

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
            writerDepth: () => 0, // DuckDbDataStore uses synchronous writes
            indexerCapacity: () => IndexerQueue.MaxDepth,
            analysisCapacity: () => AnalysisQueue.MaxDepth,
            writerCapacity: () => 0, // DuckDbDataStore uses synchronous writes
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

    public DuckDbDataStore? Database { get; }

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
        // Per-item processing is tracked via epoch activities (StartEpochActivity/CompleteEpochActivity)
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
            Volatile.Write(ref _lastError, $"{item.Uri}: {ex.Message}");
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
        if (item.Epoch < 0)
            return;

        bool needsRequeue;
        lock (_analysisLock)
        {
            // Always track items for structure embeddings (regardless of read-only status)
            if (!_pendingStructureEmbeddings.TryGetValue(item.Epoch, out var embedQueue))
            {
                embedQueue = new Queue<IndexItem>();
                _pendingStructureEmbeddings[item.Epoch] = embedQueue;
            }
            embedQueue.Enqueue(item);

            // Only track non-read-only items for multi-file analysis
            if (!item.IsReadOnly)
            {
                if (!_pendingAnalysis.TryGetValue(item.Epoch, out var analysisQueue))
                {
                    analysisQueue = new Queue<IndexItem>();
                    _pendingAnalysis[item.Epoch] = analysisQueue;
                }
                analysisQueue.Enqueue(item);
            }
            else
            {
                AddEpochTag(item.Epoch, "analysis.skip", "read_only_idle");
            }

            // If this epoch was already released, we need to re-enqueue it
            // otherwise the item will be orphaned
            needsRequeue = item.Epoch <= Interlocked.Read(ref _lastReleasedEpoch);
        }

        if (needsRequeue)
        {
            EnqueueIdleEpoch(item.Epoch);
        }
    }

    private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args)
    {
        // Complete the hot path epoch span before starting idle processing
        CompleteEpochActivity(args.Epoch, success: true);

        Metrics?.IdleCycles.Add(1);

        // FM-005 fix: Enqueue ALL epochs with pending work, not just the triggering one.
        // This handles the race condition where epoch N completes while epoch N+1 is processing,
        // causing HotPathIdle to be skipped for epoch N. By enqueuing all pending epochs here,
        // we ensure no epoch is orphaned.
        lock (_analysisLock)
        {
            foreach (var epoch in _pendingStructureEmbeddings.Keys)
            {
                EnqueueIdleEpoch(epoch);
            }
        }

        // Start a fresh epoch so subsequent work participates in idle post-processing again.
        _epochTracker.BeginNewEpoch();
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
        var activity = ActivitySource.StartActivity(ActivityKind.Internal, name: "hot_path_epoch", tags: new TagList
        {
            { "epoch", epoch }
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
                    // Check if there's actually work to do for this epoch
                    // Must check BOTH _pendingAnalysis (non-read-only items) AND _pendingStructureEmbeddings (all items including read-only)
                    bool hasWork;
                    lock (_analysisLock)
                    {
                        var hasAnalysis = _pendingAnalysis.TryGetValue(epoch, out var analysisBacklog) && analysisBacklog.Count > 0;
                        var hasEmbeddings = _pendingStructureEmbeddings.TryGetValue(epoch, out var embedBacklog) && embedBacklog.Count > 0;
                        hasWork = hasAnalysis || hasEmbeddings;
                    }

                    if (!hasWork)
                        continue;

                    try
                    {
                        // Update _lastReleasedEpoch BEFORE processing to prevent race condition:
                        // If ScheduleAnalysis adds items during ReleaseAnalysisAsync, it will see
                        // that this epoch is being released and re-enqueue it.
                        if (epoch > Interlocked.Read(ref _lastReleasedEpoch))
                            Interlocked.Exchange(ref _lastReleasedEpoch, epoch);

                        await ReleaseAnalysisAsync(epoch).ConfigureAwait(false);
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
        var startedProcessing = false;
        try
        {
            Queue<IndexItem>? structureEmbedQueue = null;
            Queue<IndexItem>? analysisQueue = null;
            lock (_analysisLock)
            {
                _pendingStructureEmbeddings.Remove(epoch, out structureEmbedQueue);
                _pendingAnalysis.Remove(epoch, out analysisQueue);

                // Atomically increment the active processing counter BEFORE releasing the lock.
                // This ensures GetPendingIdleProcessingCount() never sees zero while we have work to do.
                // The counter is decremented in the finally block.
                var hasWork = (structureEmbedQueue is not null && structureEmbedQueue.Count > 0) ||
                              (analysisQueue is not null && analysisQueue.Count > 0);
                if (hasWork)
                {
                    Interlocked.Increment(ref _activeIdleProcessingCount);
                    startedProcessing = true;
                }
            }

            if (!startedProcessing)
                return;

            var structureEmbedItems = structureEmbedQueue?.ToArray() ?? Array.Empty<IndexItem>();
            var pendingItems = analysisQueue?.ToArray() ?? Array.Empty<IndexItem>();

            // Create span for the entire idle phase processing
            using var idleSpan = ActivitySource.StartActivity("idle_processing", ActivityKind.Internal);
            idleSpan?.SetTag("epoch", epoch);
            idleSpan?.SetTag("structure_embed_count", structureEmbedItems.Length);
            idleSpan?.SetTag("analysis_count", pendingItems.Length);

            // Prune phase (uses all items including read-only for pruning stale documents)
            PruningResult pruningResult;
            using (ActivitySource.StartActivity("prune_phase", ActivityKind.Internal))
            {
                var pruneTimer = Stopwatch.StartNew();
                pruningResult = await ArtifactPruner.PruneAsync(structureEmbedItems, Shutdown.Token).ConfigureAwait(false);
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
            }

            // Structure embedding phase (fast, enables immediate semantic search)
            // Uses structureEmbedItems which includes ALL items (even read-only imports)
            using (ActivitySource.StartActivity("structure_embedding_phase", ActivityKind.Internal))
            {
                var structureTimer = Stopwatch.StartNew();
                await VectorCoordinator.GenerateStructureEmbeddingsAsync(structureEmbedItems, Shutdown.Token).ConfigureAwait(false);
                structureTimer.Stop();
                Metrics?.IdlePhaseDuration.Record(structureTimer.Elapsed.TotalMilliseconds, new TagList
                {
                    { "phase", "structure_embedding" }
                });
            }

            // NOTE: VectorCoordinator.ApplyAsync marks items for full-text embedding but doesn't flush the writer.
            // For imports, embedding refresh is triggered in RepoQlServiceImpl.ImportRepository after
            // the writer flush completes. For file watching, embeddings update on subsequent idle cycles.

            // Full-text vector refresh phase
            using (ActivitySource.StartActivity("vector_refresh_phase", ActivityKind.Internal))
            {
                var vectorTimer = Stopwatch.StartNew();
                if (pendingItems.Length > 0)
                {
                    var latest = pendingItems[0];
                    for (var i = 1; i < pendingItems.Length; i++)
                    {
                        if (pendingItems[i].Epoch > latest.Epoch)
                            latest = pendingItems[i];
                    }

                    await VectorCoordinator.ApplyAsync(latest, Shutdown.Token).ConfigureAwait(false);
                }
                vectorTimer.Stop();
                Metrics?.IdlePhaseDuration.Record(vectorTimer.Elapsed.TotalMilliseconds, new TagList
                {
                    { "phase", "vector_refresh" }
                });
            }

            // VSS HNSW index refresh phase (rebuilds in-memory HNSW indexes for fast semantic search)
            using (ActivitySource.StartActivity("vss_index_phase", ActivityKind.Internal))
            {
                var vssTimer = Stopwatch.StartNew();
                await VectorCoordinator.RefreshVssIndexAsync(Shutdown.Token).ConfigureAwait(false);
                vssTimer.Stop();
                Metrics?.IdlePhaseDuration.Record(vssTimer.Elapsed.TotalMilliseconds, new TagList
                {
                    { "phase", "vss_index" }
                });
            }

            // Multi-file analysis enqueue phase
            using (ActivitySource.StartActivity("multi_file_analysis_phase", ActivityKind.Internal))
            {
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
        }
        catch (Exception ex)
        {
            failure = ex;
            Volatile.Write(ref _lastError, $"Epoch {epoch}: {ex.Message}");
            Logger.LogError(ex, "Failed to dispatch analysis work for epoch {Epoch}", epoch);
        }
        finally
        {
            // Decrement active processing counter so WaitForPipelineAsync knows idle work is done
            if (startedProcessing)
            {
                Interlocked.Decrement(ref _activeIdleProcessingCount);
            }

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

            // Note: hot_path_epoch activity is completed in OnHotPathIdle before idle processing starts
            // The idle_processing span is tracked separately via ActivitySource.StartActivity above
        }
    }

    private Task DeleteStaleDocumentsAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count == 0)
            return Task.CompletedTask;

        if (Database is null)
        {
            Logger.LogWarning("Detected {Count} stale documents but no database is configured; catalog entries will be cleared only in-memory.", deletedArtifacts.Count);
            foreach (var uri in deletedArtifacts)
            {
                DocumentCatalog.ApplyDelete(uri);
            }
            return Task.CompletedTask;
        }

        foreach (var uri in deletedArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // DuckDbDataStore.DeleteArtifact is synchronous
            var deleted = Database.DeleteArtifact(uri);
            if (deleted)
            {
                DocumentCatalog.ApplyDelete(uri);
            }
        }

        return Task.CompletedTask;
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
            Volatile.Write(ref _lastError, $"Analysis {item.Uri}: {ex.Message}");
            LogUriFailedDuringAnalysis(Logger, ex, item.Uri);
            return;
        }
        AddEpochTag(item.Epoch, "analysis.result", "Success");
    }

    /// <summary>
    /// Handles hot-path item timeout. Called by WorkQueue when an item exceeds the configured timeout.
    /// This is critical for FM-001 mitigation: ensures epoch counters remain balanced and pipeline doesn't stall.
    /// </summary>
    private void HandleHotPathItemTimeout(IndexItem item, TimeSpan elapsed)
    {
        var mime = item.MediaType?.ToString()
                   ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString()
                   ?? "unknown";

        // Record timeout in metrics
        Metrics?.FilesErrored.Add(1, new TagList
        {
            { "mime_type", mime },
            { "error_type", "TimeoutException" },
            { "stage", "timeout" }
        });

        // Store last error for diagnostics
        Volatile.Write(ref _lastError, $"{item.Uri}: Timed out after {elapsed.TotalSeconds:F1}s");

        // Add epoch tag for tracing
        AddEpochTag(item.Epoch, "index.result", "timeout");
        AddEpochTag(item.Epoch, "index.timeout_duration_ms", elapsed.TotalMilliseconds);

        // CRITICAL: Decrement epoch counter to prevent epoch imbalance (FM-003)
        // The item was incremented when enqueued, and normally decremented in IndexItemAsync's finally block.
        // Since IndexItemAsync was cancelled mid-processing, we must decrement here.
        var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
        if (epochBecameIdle && State == IndexingState.AllIdle)
        {
            try
            {
                HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HotPathIdle handler failed for epoch {Epoch} during timeout handling", item.Epoch);
            }
        }

        LogItemTimedOut(Logger, item.Uri, elapsed.TotalSeconds);
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
            var xKey = RepoUri.Normalize(x.Uri.Container.AbsoluteUri);
            var yKey = RepoUri.Normalize(y.Uri.Container.AbsoluteUri);
            return StringComparer.OrdinalIgnoreCase.Equals(xKey, yKey) && x.Options == y.Options;
        }

        public int GetHashCode(IndexItem obj)
        {
            if (obj is null)
                return 0;
            var key = RepoUri.Normalize(obj.Uri.Container.AbsoluteUri).ToLowerInvariant();
            return HashCode.Combine(key, obj.Options);
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

    [LoggerMessage(LogLevel.Warning, "{Uri} timed out after {ElapsedSeconds:F1}s (FM-001 mitigation: item skipped, epoch counter decremented)")]
    static partial void LogItemTimedOut(ILogger<IndexingEngine> logger, RepoUri uri, double elapsedSeconds);
    #endregion
}
