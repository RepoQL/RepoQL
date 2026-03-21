using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
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
using static RepoQL.Contracts.Embeddings.EmbeddingModeExtensions;

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
    public int IndexingWorkers { get; set; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Maximum capacity of the hot-path work queue. Backpressure applied when full.
    /// Default: 10,000 items.
    /// </summary>
    public int IndexingQueueSize {  get; set; } = 10_000;

    /// <summary>
    /// Number of concurrent workers for idle processing (Multi-file Analysis, Index Rebuild).
    /// Default: min(<see cref="Environment.ProcessorCount"/>, 8).
    /// </summary>
    public int AnalysisWorkers { get; set; } = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    /// <summary>
    /// Maximum capacity of the idle-processing work queue. Larger than hot-path because
    /// multi-file operations can spawn many items per batch.
    /// Default: 100,000 items.
    /// </summary>
    public int AnalysisQueueSize {  get; set; } = 100_000;

    /// <summary>
    /// Maximum time allowed for processing a single item in the hot-path queue.
    /// If an item exceeds this duration, it is considered timed out and skipped.
    /// This prevents stuck items from blocking the entire pipeline (FM-001 mitigation).
    /// Default: 45 seconds. Files that cannot complete within this window are deferred out of
    /// the hot path so the host remains responsive.
    /// Set to null to disable per-item timeout (not recommended).
    /// </summary>
    public TimeSpan? HotPathItemTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Maximum time allowed for processing a single item in the analysis queue.
    /// Analysis items typically involve multi-file operations and may take longer.
    /// Default: 10 minutes. Set to null to disable timeout.
    /// </summary>
    public TimeSpan? AnalysisItemTimeout { get; set; } = TimeSpan.FromMinutes(10);
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
/// (pruning, embedding refresh, multi-file analysis).
/// </para>
///
/// <para><strong>Threading Model</strong></para>
/// <list type="bullet">
/// <item><description>Hot path: Concurrent (ProcessorCount × 2 workers)</description></item>
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
/// <item><description>Pruner runs BEFORE embedding refresh</description></item>
/// <item><description>Analysis sees ONLY committed graph state</description></item>
/// </list>
///
/// <para>See docs/ARCHITECTURE.md for design rationale and docs/JOURNEY.md for complete file flow example.</para>
/// </remarks>
public partial class IndexingEngine : IAsyncDisposable
{
    private const string TelemetrySourceName = "RepoQL.Indexing";
    private const int DeferredRetryWorkers = 1;
    private static readonly IndexingState HotPathBusyMask =
        IndexingState.ClassificationBusy |
        IndexingState.ParsingBusy |
        IndexingState.SingleFileAnalysisBusy;
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
    private readonly object _structureEmbeddingLock = new();
    private readonly Dictionary<long, Queue<IndexItem>> _pendingAnalysis = new();
    private readonly Dictionary<long, Queue<IndexItem>> _pendingStructureEmbeddings = new();  // Separate from analysis - includes read-only items
    private readonly Dictionary<long, HashSet<RepoUri>> _observedUrisByEpoch = new();
    private readonly Queue<IndexItem> _pendingDeferredHotPathRetries = new();
    private readonly ConcurrentDictionary<string, byte> _deferredRetryOwnership = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, int> _pendingEagerStructureEmbeddings = new();
    private readonly Dictionary<long, TaskCompletionSource<bool>> _structureEmbeddingEpochCompletion = new();
    private readonly ConcurrentDictionary<string, IndexItemOptions> _requeueRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<long> _analysisEpochChannel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _idleProcessingTask;
    private readonly Channel<IndexItem>? _structureEmbeddingChannel;
    private readonly Task? _structureEmbeddingWorker;
    private readonly ConcurrentDictionary<long, Activity> _epochActivities = new();
    private readonly Dictionary<IndexingState, StageCounter> _stageCounters = new();
    private bool _disposed;
    private long _lastReleasedEpoch = long.MinValue;
    private int _activeIdleProcessingCount;
    private string? _lastError;
    private long _firstEpochStartTicks;
    private int _readyLogged;
    private int _indexerQueueFaulted;
    private int _analysisQueueFaulted;
    private int _deferredRetryQueueFaulted;
    private int _deferredToIdleCount;
    private int _deferredRetryWakeScheduled;

    /// <summary>
    /// Optional callback invoked at each lifecycle milestone during idle processing.
    /// Called from worker threads — must be thread-safe.
    /// </summary>
    public Action<string, string?>? MilestoneCallback { get; set; }
    private IArtifactPruner ArtifactPruner { get; }
    internal IEmbeddingCoordinator EmbeddingCoordinator { get; }
    private UriRegistry? UriRegistry { get; }
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly EmbeddingMode _embeddingMode;
    private bool StructureEmbeddingsEnabled => _embeddingMode.IncludesStructure() && _embeddingProvider is { Enabled: true };

    // Diagnostic accessors (used by IndexingEngineDiagnosticsProvider)
    internal int ActiveIdleProcessingCount => Volatile.Read(ref _activeIdleProcessingCount);
    internal string? LastError => Volatile.Read(ref _lastError);
    internal long CurrentEpoch => _epochTracker.CurrentEpoch;
    internal int HotPathTimeoutCount => IndexerQueue.TimeoutCount;
    internal int AnalysisTimeoutCount => AnalysisQueue.TimeoutCount;
    internal int DeferredRetryTimeoutCount => DeferredRetryQueue.TimeoutCount;
    internal int DeferredToIdleCount => Volatile.Read(ref _deferredToIdleCount);

    /// <summary>
    /// Gets items currently being processed in the hot-path queue with their durations.
    /// Useful for diagnosing potentially stuck items.
    /// </summary>
    internal IReadOnlyList<WorkQueueInFlightItem<IndexItem>> GetHotPathInFlightItems()
        => IndexerQueue.GetInFlightItems();

    /// <summary>
    /// Gets items currently being processed in the analysis queue with their durations.
    /// </summary>
    internal IReadOnlyList<WorkQueueInFlightItem<IndexItem>> GetAnalysisInFlightItems()
        => AnalysisQueue.GetInFlightItems();

    internal IReadOnlyList<WorkQueueInFlightItem<IndexItem>> GetDeferredRetryInFlightItems()
        => DeferredRetryQueue.GetInFlightItems();

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
            count += _pendingDeferredHotPathRetries.Count;
            count += DeferredRetryQueue.Depth;
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

    internal IReadOnlyList<IndexItem> GetPendingDeferredRetryItems()
    {
        lock (_analysisLock)
        {
            var pending = _pendingDeferredHotPathRetries.ToList();
            pending.AddRange(DeferredRetryQueue.GetPendingItems());
            return pending;
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
        var key = GetQueueKey(indexItem.Uri);

        var incremented = false;
        try
        {
            if (_deferredRetryOwnership.ContainsKey(key))
            {
                MarkRequeue(indexItem);
                return false;
            }

            var enqueued = await IndexerQueue.EnqueueAsync(indexItem, cancellationToken).ConfigureAwait(false);
            if (!enqueued)
            {
                MarkRequeue(indexItem);
                return false;
            }

            _epochTracker.Increment(epoch);
            TrackObservedUri(epoch, indexItem.Uri);
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

    private static string GetQueueKey(RepoUri uri)
        => RepoUri.NormalizeContainer(uri);

    private static IndexItemOptions MergeOptions(IndexItemOptions existing, IndexItemOptions incoming)
    {
        if (existing == IndexItemOptions.Always || incoming == IndexItemOptions.Always)
            return IndexItemOptions.Always;
        return existing & incoming;
    }

    private void MarkRequeue(IndexItem item)
    {
        var key = GetQueueKey(item.Uri);
        _requeueRequested.AddOrUpdate(
            key,
            item.Options,
            (_, existing) => MergeOptions(existing, item.Options));
    }

    private void TryRequeue(IndexItem completedItem)
    {
        var key = GetQueueKey(completedItem.Uri);
        if (!_requeueRequested.TryGetValue(key, out var options))
            return;

        if (_deferredRetryOwnership.ContainsKey(key))
            return;

        if (!_requeueRequested.TryRemove(key, out options))
            return;

        CancellationToken shutdownToken;
        try
        {
            shutdownToken = Shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnqueueIndexItemAsync(CreateRetryIndexItem(completedItem, options), shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to requeue {Uri} after dedupe drop.", completedItem.Uri);
            }
        });
    }

    private static RawArtifact RecreateRawArtifact(RawArtifact rawArtifact)
        => new(rawArtifact.FileSystem.GetFile(rawArtifact.Uri), rawArtifact.FileSystem);

    private static IndexItem CreateRetryIndexItem(IndexItem item, IndexItemOptions options)
        => new(RecreateRawArtifact(item.RawArtifact), options);

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
        IEmbeddingCoordinator? embeddingCoordinator = null,
        IndexingEngineOptions? options = null,
        ILogger<IndexingEngine>? logger = null,
        IndexingMetrics? metrics = null,
        UriRegistry? uriRegistry = null,
        IEmbeddingProvider? embeddingProvider = null,
        EmbeddingMode embeddingMode = EmbeddingMode.Full)
    {
        Database = db;
        Filter = filter ?? new RepoGitIgnoreFilter(".");
        UriRegistry = uriRegistry;
        Classifier = classifier ?? new ClassificationPipeline( []);
        Parser = parser ?? new ParsingPipeline([]);
        SingleFileAnalyzer = singleFileAnalyzer ?? new SingleFileAnalysisPipeline([]);
        MultiFileAnalyzer = multiFileAnalyzer ??  new MultiFileAnalysisPipeline([]);
        IndexRebuilder = indexRebuilder ?? new IndexRebuildPipeline([]);
        DocumentCatalog = documentCatalog ?? new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        Committer = committer ?? NullIndexingCommitter.Instance;
        ArtifactPruner = artifactPruner ?? NullArtifactPruner.Instance;
        EmbeddingCoordinator = embeddingCoordinator ?? NullEmbeddingCoordinator.Instance;
        Options = options ??  new IndexingEngineOptions();
        Logger = logger ?? NullLogger<IndexingEngine>.Instance;
        Metrics = metrics;
        _embeddingProvider = embeddingProvider;
        _embeddingMode = embeddingMode;
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
            OnItemTimeout = HandleHotPathItemTimeout,
            OnQueueFault = HandleIndexerQueueFault,
            OnItemCompleted = _ => ScheduleDeferredRetryDrain()
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
        AnalysisQueue.OnItemTimeout = HandleAnalysisItemTimeout;
        AnalysisQueue.OnQueueFault = HandleAnalysisQueueFault;
        DeferredRetryQueue = new WorkQueue<IndexItem>(
            "DeferredRetryQueue",
            Options.AnalysisQueueSize,
            DeferredRetryWorkers,
            async (item, c) => { await IndexItemAsync(item, c); },
            Shutdown.Token,
            itemTimeout: Options.AnalysisItemTimeout,
            meter: null,
            comparer: new IndexItemComparer(),
            logger: Logger);
        DeferredRetryQueue.OnItemTimeout = HandleDeferredRetryItemTimeout;
        DeferredRetryQueue.OnQueueFault = HandleDeferredRetryQueueFault;
        if (StructureEmbeddingsEnabled)
        {
            _structureEmbeddingChannel = Channel.CreateBounded<IndexItem>(
                new BoundedChannelOptions(Options.AnalysisQueueSize)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
            _structureEmbeddingWorker = Task.Run(() => ProcessStructureEmbeddingBatchLoopAsync(Shutdown.Token));
        }

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
        _idleProcessingTask = Task.Run(() =>
            IdleLoopSupervisor.RunAsync(
                loopName: "idle processing",
                runLoopAsync: _ => ProcessIdleEpochsAsync(),
                onFailure: ex => Volatile.Write(ref _lastError, $"Idle processing: {ex.Message}"),
                logger: Logger,
                cancellationToken: Shutdown.Token));

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

    internal WorkQueue<IndexItem> DeferredRetryQueue { get; }

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

        try
        {
            await DeferredRetryQueue.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        if (_structureEmbeddingChannel is not null)
        {
            try
            {
                _structureEmbeddingChannel.Writer.TryComplete();
                if (_structureEmbeddingWorker is not null)
                {
                    // Bounded shutdown — don't hang if ONNX inference is stuck
                    var completed = await Task.WhenAny(_structureEmbeddingWorker, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    if (completed != _structureEmbeddingWorker)
                    {
                        Logger.LogWarning("Structure embedding worker did not finish within 2s grace period");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

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

        // Record wall-clock start time for the very first epoch (used for "indexing ready" log)
        if (epoch == 1)
            Interlocked.CompareExchange(ref _firstEpochStartTicks, Stopwatch.GetTimestamp(), 0);

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
            item.SetCurrentOperation(currentStage);
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

            // FM-002 mitigation: Track per-operation timing for slow operation detection
            currentStage = "catalog_init";
            item.SetCurrentOperation(currentStage);
            var catalogTimer = Stopwatch.StartNew();
            await DocumentCatalog.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            catalogTimer.Stop();
            RecordOperationDuration("catalog_init", catalogTimer.Elapsed, item);

            currentStage = "digest";
            item.SetCurrentOperation(currentStage);
            var digestTimer = Stopwatch.StartNew();
            var digestHex = await item.RawArtifact.Digest.WithCancellation(cancellationToken).ConfigureAwait(false);
            digestTimer.Stop();
            RecordOperationDuration("digest", digestTimer.Elapsed, item);

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

                // Ensure UriRegistry reflects the file's already-indexed state.
                // Without this, files registered as Discovered by import operations
                // stay Pending and the Operation never completes.
                UriRegistry?.SetSkippedUpToDate(item.Uri);

                return;
            }

            // Queue commands can mark a URI as Failed/Skipped while it is waiting.
            // Respect terminal states before transitioning to Indexing.
            if (ShouldAbortAtStageBoundary(item.Uri, out _))
            {
                status = "filtered";
                AddEpochTag(item.Epoch, "index.abort_boundary", "before_indexing");
                return;
            }

            // Update URI registry to track indexing state - only after filtering and up-to-date checks
            UriRegistry?.SetIndexing(item.Uri);

            try
            {
                DocumentCatalog.BeginProcessing(item.Uri, digestHex);
                catalogRegistered = true;

                currentStage = "pipeline";
                item.SetCurrentOperation(currentStage);
                item.ClearFailureDetail();
                var result = await ApplyIndexerPipeline(item, cancellationToken);
                if (result == PipelineResult.Cancelled && cancellationToken.IsCancellationRequested && !Shutdown.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                mime = item.MediaType?.ToString() ?? mime;
                status = result.ToString();
                RecordResult(item.Epoch, result);
                if (result == PipelineResult.Error)
                    UriRegistry?.SetFailed(item.Uri, BuildPipelineFailureMessage(item, currentStage, result));
                if (result != PipelineResult.Success)
                    return;

                if (item.IsTimedOut)
                {
                    status = "timed_out_late";
                    AddEpochTag(item.Epoch, "index.late_abort", "after_pipeline");
                    return;
                }

                if (ShouldAbortAtStageBoundary(item.Uri, out _))
                {
                    status = "filtered";
                    AddEpochTag(item.Epoch, "index.abort_boundary", "before_commit");
                    return;
                }

                currentStage = "commit";
                item.SetCurrentOperation(currentStage);
                var commitTimer = Stopwatch.StartNew();
                var commitResult = await Committer.CommitAsync(item, cancellationToken).ConfigureAwait(false);
                commitTimer.Stop();

                if (commitResult == CommitOutcome.Skipped)
                {
                    RecordStageDuration("commit", commitTimer.Elapsed.TotalMilliseconds, PipelineResult.Error, item);
                    status = "commit_skipped";
                    return;
                }

                RecordStageDuration("commit", commitTimer.Elapsed.TotalMilliseconds, PipelineResult.Success, item);

                if (item.IsTimedOut)
                {
                    status = "timed_out_late";
                    AddEpochTag(item.Epoch, "index.late_abort", "after_commit");
                    return;
                }

                // Record successful indexing
                Metrics?.FilesIndexed.Add(1, new TagList
                {
                    { "mime_type", mime },
                    { "status", "success" }
                });

                status = "success";

                // Update URI registry with indexed status and symbols
                if (UriRegistry is not null)
                {
                    var symbols = ExtractSymbolsFromRecords(item.Records);
                    var lineCount = ExtractLineCount(item.Records);
                    var (headline, structure) = ExtractXraySummaries(item.Records);
                    UriRegistry.SetIndexed(item.Uri, lineCount, symbols, headline, structure);
                }

                ScheduleEagerStructureEmbedding(item);
                ScheduleAnalysis(item);

                // Release heavy payload data now that everything is persisted to DuckDB.
                // The property bag (DocumentModel with full file text, syntax trees) and
                // annotations are no longer needed. Records are preserved for idle processing.
                item.ReleasePostCommitPayload();

                // NOTE: Once WriteOperation dispatch is in place, hook DocumentCatalog.ApplyUpsert/Delete
                //       through the writer's OnCommitted callback to keep the cache authoritative.
            }
            finally
            {
                if (catalogRegistered)
                    DocumentCatalog.CompleteProcessing(item.Uri);
            }
        }
        catch (OperationCanceledException) when (!Shutdown.IsCancellationRequested)
        {
            status = "timed_out";
            item.MarkSkipEpochCompletion();
            throw;
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            LogIndexingCancelledForItem(Logger, item.Name);
            return;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // File was deleted between discovery and indexing — not an error, just a state transition.
            status = "pruned";
            Metrics?.FilesFiltered.Add(1, new TagList
            {
                { "reason", "deleted_before_indexing" },
                { "mime_type", mime }
            });
            LogFileDeletedBeforeIndexing(Logger, item.Uri);
            UriRegistry?.RemoveFile(item.Uri);
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

            // Update URI registry with failed status
            UriRegistry?.SetFailed(item.Uri, $"{currentStage}: {ex.Message}");
            return;
        }
        finally
        {
            if (item.IsDeferredRetry)
            {
                _deferredRetryOwnership.TryRemove(GetQueueKey(item.Uri), out _);
            }

            item.SetCurrentOperation(null);
            overallTimer.Stop();
            UriRegistry?.SetProcessingDuration(item.Uri, overallTimer.Elapsed.TotalMilliseconds);
            Metrics?.HotPathDuration.Record(overallTimer.Elapsed.TotalMilliseconds, new TagList
            {
                { "status", status },
                { "mime_type", mime },
                { "read_only", item.IsReadOnly.ToString().ToLowerInvariant() }
            });
            Metrics?.RecordFileProcessed(mime, status, fileSize, overallTimer.Elapsed.TotalMilliseconds);
            AddEpochTag(item.Epoch, "index.result", status);
            if (!item.SkipEpochCompletion && item.TryMarkEpochComplete())
            {
                var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
                if (epochBecameIdle)
                {
                    if (State == IndexingState.AllIdle)
                        HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
                    else
                        EnqueueIdleEpoch(item.Epoch); // Don't lose the epoch when other stages are still active
                }
            }
            TryRequeue(item);
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

    /// <summary>
    /// Records operation duration and logs warning for slow operations (FM-002 observability).
    /// </summary>
    /// <remarks>
    /// Operations exceeding 30 seconds are logged as warnings to help identify
    /// potential hangs before they reach the per-item timeout (FM-001).
    /// </remarks>
    private void RecordOperationDuration(string operation, TimeSpan elapsed, IndexItem item)
    {
        var mime = item.MediaType?.ToString()
                   ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString()
                   ?? "unknown";

        // Record as stage duration metric for consistency
        Metrics?.StageDuration.Record(elapsed.TotalMilliseconds, new TagList
        {
            { "stage", operation },
            { "status", "Success" },
            { "mime", mime },
            { "read_only", item.IsReadOnly }
        });

        // FM-002: Warn on slow operations (>30s) to help identify potential hangs
        if (elapsed.TotalSeconds > SlowOperationThresholdSeconds)
        {
            LogSlowOperation(Logger, operation, item.Uri, elapsed.TotalSeconds, SlowOperationThresholdSeconds);
        }
    }

    private void ScheduleEagerStructureEmbedding(IndexItem item)
    {
        if (_structureEmbeddingChannel is null)
            return;

        TrackEagerStructureEmbedding(item.Epoch);
        _ = EnqueueEagerStructureEmbeddingAsync(item);
    }

    private async Task EnqueueEagerStructureEmbeddingAsync(IndexItem item)
    {
        if (_structureEmbeddingChannel is null)
            return;

        try
        {
            await _structureEmbeddingChannel.Writer.WriteAsync(item, Shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
        {
            CompleteEagerStructureEmbedding(item.Epoch);
        }
        catch (Exception ex)
        {
            CompleteEagerStructureEmbedding(item.Epoch);
            UriRegistry?.SetEmbeddingFailed(item.Uri, $"structure embedding enqueue failed: {ex.Message}");
            Logger.LogWarning(ex, "Failed to enqueue eager structure embedding for {Uri}", item.Uri);
        }
    }

    private async Task ProcessStructureEmbeddingBatchLoopAsync(CancellationToken cancellationToken)
    {
        const int maxBatchSize = 100;
        var debounceDelay = TimeSpan.FromMilliseconds(100);
        var batch = new List<IndexItem>(maxBatchSize);

        try
        {
            while (await _structureEmbeddingChannel!.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.Clear();

                // Read first item (guaranteed available after WaitToReadAsync returns true)
                if (!_structureEmbeddingChannel.Reader.TryRead(out var first))
                    continue;
                batch.Add(first);

                // Drain items already available before debouncing
                while (batch.Count < maxBatchSize && _structureEmbeddingChannel.Reader.TryRead(out var ready))
                    batch.Add(ready);

                // Only debounce if batch is small (more items likely arriving)
                if (batch.Count < maxBatchSize)
                {
                    try
                    {
                        await Task.Delay(debounceDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Shutting down — process whatever we have
                    }

                    // Drain items that arrived during debounce
                    while (batch.Count < maxBatchSize && _structureEmbeddingChannel.Reader.TryRead(out var item))
                        batch.Add(item);
                }

                // Process batch with timeout protection
                try
                {
                    using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (Options.AnalysisItemTimeout.HasValue)
                        batchCts.CancelAfter(Options.AnalysisItemTimeout.Value);

                    await EmbeddingCoordinator.GenerateStructureEmbeddingsAsync(batch, batchCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // Real shutdown — propagate
                }
                catch (OperationCanceledException)
                {
                    // Batch timeout — log and let catch-up handle it
                    Logger.LogWarning("Structure embedding batch timed out for {Count} items", batch.Count);
                    foreach (var item in batch)
                        UriRegistry?.SetEmbeddingFailed(item.Uri, "structure embedding batch timed out");
                }
                catch (Exception ex)
                {
                    foreach (var item in batch)
                        UriRegistry?.SetEmbeddingFailed(item.Uri, $"structure embedding failed: {ex.Message}");
                    Logger.LogWarning(ex, "Batch structure embedding failed for {Count} items", batch.Count);
                }
                finally
                {
                    foreach (var item in batch)
                        CompleteEagerStructureEmbedding(item.Epoch);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Structure embedding batch worker faulted unexpectedly");
        }
        finally
        {
            // Drain any items remaining in the channel so their epoch tracking is completed.
            // Without this, shutdown can leave epoch completion TCSes permanently unsignaled.
            while (_structureEmbeddingChannel!.Reader.TryRead(out var abandoned))
                CompleteEagerStructureEmbedding(abandoned.Epoch);
        }
    }

    private void TrackEagerStructureEmbedding(long epoch)
    {
        if (epoch < 0)
            return;

        lock (_structureEmbeddingLock)
        {
            _pendingEagerStructureEmbeddings.TryGetValue(epoch, out var pending);
            _pendingEagerStructureEmbeddings[epoch] = pending + 1;
            if (!_structureEmbeddingEpochCompletion.ContainsKey(epoch))
            {
                _structureEmbeddingEpochCompletion[epoch] =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void CompleteEagerStructureEmbedding(long epoch)
    {
        if (epoch < 0)
            return;

        TaskCompletionSource<bool>? completion = null;

        lock (_structureEmbeddingLock)
        {
            if (!_pendingEagerStructureEmbeddings.TryGetValue(epoch, out var pending))
                return;

            if (pending <= 1)
            {
                _pendingEagerStructureEmbeddings.Remove(epoch);
                if (_structureEmbeddingEpochCompletion.Remove(epoch, out var waiter))
                {
                    completion = waiter;
                }
            }
            else
            {
                _pendingEagerStructureEmbeddings[epoch] = pending - 1;
            }
        }

        completion?.TrySetResult(true);
    }

    private async Task WaitForEagerStructureEmbeddingsAsync(IReadOnlyList<long> epochs, CancellationToken cancellationToken)
    {
        if (_structureEmbeddingChannel is null || epochs.Count == 0)
            return;

        List<Task>? waitTasks = null;
        lock (_structureEmbeddingLock)
        {
            foreach (var epoch in epochs.Distinct())
            {
                if (!_pendingEagerStructureEmbeddings.TryGetValue(epoch, out var pending) || pending <= 0)
                    continue;

                if (!_structureEmbeddingEpochCompletion.TryGetValue(epoch, out var waiter))
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _structureEmbeddingEpochCompletion[epoch] = waiter;
                }

                waitTasks ??= [];
                waitTasks.Add(waiter.Task);
            }
        }

        if (waitTasks is null || waitTasks.Count == 0)
            return;

        await Task.WhenAll(waitTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<IndexItem> GetStructureEmbeddingCatchupItems(IReadOnlyList<IndexItem> items)
    {
        if (!StructureEmbeddingsEnabled || items.Count == 0 || UriRegistry is null)
            return [];

        var pending = new List<IndexItem>(items.Count);
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!seenUris.Add(item.Uri.AbsoluteUri))
                continue;

            if (!UriRegistry.TryGetValue(item.Uri, out var entry))
            {
                pending.Add(item);
                continue;
            }

            if (entry.EmbeddingStatus is EmbeddingStatus.Embedded or EmbeddingStatus.NotApplicable)
                continue;

            pending.Add(item);
        }

        return pending;
    }

    private void RecordMilestone(string name, string? detail = null)
    {
        try
        {
            MilestoneCallback?.Invoke(name, detail);
        }
        catch
        {
            // Milestone callbacks should never fail engine operations.
        }
    }

    /// <summary>
    /// Threshold in seconds for logging slow operation warnings.
    /// Operations exceeding this duration are logged to help identify bottlenecks.
    /// </summary>
    private const double SlowOperationThresholdSeconds = 30.0;

    private void ScheduleAnalysis(IndexItem item)
    {
        if (item.Epoch < 0)
            return;

        // Don't accumulate idle work after a terminal fault — it can never be processed.
        if (Volatile.Read(ref _indexerQueueFaulted) == 1)
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

        var epochItems = _epochTracker.GetEpochTotalItems(args.Epoch);
        var epochElapsed = _epochTracker.GetEpochElapsed(args.Epoch);
        RecordMilestone("hot_path_complete", $"{epochItems} files, {epochElapsed.TotalSeconds:F1}s");

        Metrics?.IdleCycles.Add(1);

        // FM-005 fix: Enqueue ALL epochs with pending work, not just the triggering one.
        // This handles the race condition where epoch N completes while epoch N+1 is processing,
        // causing HotPathIdle to be skipped for epoch N. By enqueuing all pending epochs here,
        // we ensure no epoch is orphaned.
        var epochsToRelease = new HashSet<long> { args.Epoch };
        lock (_analysisLock)
        {
            foreach (var epoch in _pendingStructureEmbeddings.Keys)
                epochsToRelease.Add(epoch);

            foreach (var epoch in _pendingAnalysis.Keys)
                epochsToRelease.Add(epoch);

            if (_pendingDeferredHotPathRetries.Count > 0)
                epochsToRelease.Add(args.Epoch);
        }

        foreach (var epoch in epochsToRelease)
        {
            EnqueueIdleEpoch(epoch);
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
                // FM-010 fix: Drain ALL pending epochs from the channel to prevent starvation.
                // When file changes occur rapidly, epochs can queue up faster than embedding
                // can process them. By draining all epochs at once and consolidating their
                // items, we ensure we catch up during bursts instead of falling further behind.
                var epochsToDrain = new List<long>();
                while (reader.TryRead(out var epoch))
                {
                    epochsToDrain.Add(epoch);
                }

                if (epochsToDrain.Count == 0)
                    continue;

                // Log when consolidating multiple epochs (helps diagnose starvation)
                if (epochsToDrain.Count > 1)
                {
                    Logger.LogDebug(
                        "FM-010: Consolidating {EpochCount} epochs ({MinEpoch}-{MaxEpoch}) to prevent embedding starvation.",
                        epochsToDrain.Count, epochsToDrain.Min(), epochsToDrain.Max());
                }

                try
                {
                    // Update _lastReleasedEpoch to the highest epoch we're processing
                    var maxEpoch = epochsToDrain.Max();
                    if (maxEpoch > Interlocked.Read(ref _lastReleasedEpoch))
                        Interlocked.Exchange(ref _lastReleasedEpoch, maxEpoch);

                    await ReleaseConsolidatedAnalysisAsync(epochsToDrain).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Idle post-processing failed for epochs {MinEpoch}-{MaxEpoch}.",
                        epochsToDrain.Min(), epochsToDrain.Max());
                }
            }
        }
        catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Releases analysis for multiple epochs consolidated into a single batch.
    /// This is the FM-010 fix for embedding starvation - by processing all pending
    /// epochs together, we prevent unbounded queue growth during rapid file changes.
    /// </summary>
    private async Task ReleaseConsolidatedAnalysisAsync(List<long> epochs)
    {
        var epochTimer = Stopwatch.StartNew();
        Exception? failure = null;
        var startedProcessing = false;
        var completedProcessing = false;
        var consolidatedStructureItems = new List<IndexItem>();
        var consolidatedAnalysisItems = new List<IndexItem>();
        var observedUris = new HashSet<RepoUri>();
        IndexItem[] structureEmbedItems = [];
        IndexItem[] pendingItems = [];
        IReadOnlyCollection<RepoUri> observedEpochUris = Array.Empty<RepoUri>();
        var analysisItemsEnqueued = 0;

        try
        {
            lock (_analysisLock)
            {
                // Collect items from ALL epochs being processed
                foreach (var epoch in epochs)
                {
                    if (_pendingStructureEmbeddings.Remove(epoch, out var structureQueue))
                    {
                        consolidatedStructureItems.AddRange(structureQueue);
                    }
                    if (_pendingAnalysis.Remove(epoch, out var analysisQueue))
                    {
                        consolidatedAnalysisItems.AddRange(analysisQueue);
                    }

                    if (_observedUrisByEpoch.TryGetValue(epoch, out var epochObservedUris))
                    {
                        observedUris.UnionWith(epochObservedUris);
                    }
                }

                var hasWork = consolidatedStructureItems.Count > 0
                    || consolidatedAnalysisItems.Count > 0;
                if (hasWork)
                {
                    Interlocked.Increment(ref _activeIdleProcessingCount);
                    startedProcessing = true;
                }
            }

            if (!startedProcessing)
            {
                // FM-008 visibility: Log when idle processing is skipped due to empty items.
                // This can happen if all items in the epoch failed or were filtered during hot path.
                // Pruning will NOT run in this case - stale documents may remain if this is a reindex.
                Logger.LogDebug(
                    "Idle processing skipped for epochs {MinEpoch}-{MaxEpoch}: no items pending. " +
                    "If this occurs during reindex, stale documents may not be pruned.",
                    epochs.Min(), epochs.Max());
                return;
            }

            structureEmbedItems = consolidatedStructureItems.ToArray();
            pendingItems = consolidatedAnalysisItems.ToArray();
            observedEpochUris = [.. observedUris];
            var maxEpoch = epochs.Max();

            // Create span for the entire idle phase processing
            using var idleSpan = ActivitySource.StartActivity("idle_processing", ActivityKind.Internal);
            idleSpan?.SetTag("epochs_consolidated", epochs.Count);
            idleSpan?.SetTag("epoch_min", epochs.Min());
            idleSpan?.SetTag("epoch_max", maxEpoch);
            idleSpan?.SetTag("structure_embed_count", structureEmbedItems.Length);
            idleSpan?.SetTag("analysis_count", pendingItems.Length);

            // Prune phase (uses all items including read-only for pruning stale documents)
            PruningResult pruningResult;
            using (ActivitySource.StartActivity("prune_phase", ActivityKind.Internal))
            {
                var pruneTimer = Stopwatch.StartNew();
                pruningResult = await ArtifactPruner.PruneAsync(observedEpochUris, Shutdown.Token).ConfigureAwait(false);
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
                    await EmbeddingCoordinator.ApplyDeletesAsync(pruningResult.DeletedArtifacts, Shutdown.Token).ConfigureAwait(false);
                }

                RecordMilestone("prune", $"{prunedCount} removed, {pruneTimer.Elapsed.TotalMilliseconds:F1}ms");
            }

            // Structure embedding barrier:
            // eager embedding starts immediately after commit; idle waits for those tasks
            // to complete for the consolidated epochs before proceeding.
            try
            {
                using (ActivitySource.StartActivity("structure_embedding_phase", ActivityKind.Internal))
                {
                    var structureTimer = Stopwatch.StartNew();
                    await WaitForEagerStructureEmbeddingsAsync(epochs, Shutdown.Token).ConfigureAwait(false);
                    var catchupItems = GetStructureEmbeddingCatchupItems(structureEmbedItems);
                    if (catchupItems.Count > 0)
                    {
                        // Safety net: rerun structure embedding in idle for items still not embedded
                        // after eager execution (for example, transient provider/queue failures).
                        await EmbeddingCoordinator.GenerateStructureEmbeddingsAsync(catchupItems, Shutdown.Token).ConfigureAwait(false);
                    }
                    structureTimer.Stop();
                    Metrics?.IdlePhaseDuration.Record(structureTimer.Elapsed.TotalMilliseconds, new TagList
                    {
                        { "phase", "structure_embedding" }
                    });

                    RecordMilestone("structure_embeddings",
                        $"{structureEmbedItems.Length} items confirmed, {catchupItems.Count} retried, {structureTimer.Elapsed.TotalMilliseconds:F1}ms");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Structure embedding failed for {Count} items. Items will proceed without embeddings.", structureEmbedItems.Length);
            }

            // Full-text embedding refresh phase
            try
            {
                using (ActivitySource.StartActivity("embedding_refresh_phase", ActivityKind.Internal))
                {
                    var embeddingTimer = Stopwatch.StartNew();
                    if (pendingItems.Length > 0)
                    {
                        await EmbeddingCoordinator.ApplyAsync(pendingItems, Shutdown.Token).ConfigureAwait(false);
                    }
                    embeddingTimer.Stop();
                    Metrics?.IdlePhaseDuration.Record(embeddingTimer.Elapsed.TotalMilliseconds, new TagList
                    {
                        { "phase", "embedding_refresh" }
                    });

                    RecordMilestone("embedding_refresh", $"{pendingItems.Length} items, {embeddingTimer.Elapsed.TotalMilliseconds:F1}ms");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Embedding refresh failed. Full-text search may be incomplete until next refresh.");
            }

            // Multi-file analysis enqueue phase
            using (ActivitySource.StartActivity("multi_file_analysis_phase", ActivityKind.Internal))
            {
                var analysisEnqueueTimer = Stopwatch.StartNew();
                foreach (var item in pendingItems)
                {
                    await AnalysisQueue.EnqueueAsync(item, Shutdown.Token).ConfigureAwait(false);
                    analysisItemsEnqueued++;
                }
                analysisEnqueueTimer.Stop();
                Metrics?.IdlePhaseDuration.Record(analysisEnqueueTimer.Elapsed.TotalMilliseconds, new TagList
                {
                    { "phase", "multi_file_analysis" }
                });

            }

            completedProcessing = true;

        }
        catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
            Volatile.Write(ref _lastError, $"Epochs {epochs.Min()}-{epochs.Max()}: {ex.Message}");
            Logger.LogError(ex, "Failed to dispatch analysis work for epochs {MinEpoch}-{MaxEpoch}", epochs.Min(), epochs.Max());

            if (startedProcessing)
            {
                var (requeuedStructureCount, requeuedAnalysisCount, requeuedEpochCount) =
                    RequeueIdleBacklogAfterFailure(structureEmbedItems, pendingItems, analysisItemsEnqueued);

                if (requeuedEpochCount > 0)
                {
                    Logger.LogWarning(
                        "Requeued idle backlog after failure: {StructureCount} structure items, {AnalysisCount} analysis items across {EpochCount} epochs.",
                        requeuedStructureCount,
                        requeuedAnalysisCount,
                        requeuedEpochCount);
                }
            }
        }
        finally
        {
            if (startedProcessing)
            {
                Interlocked.Decrement(ref _activeIdleProcessingCount);
            }

            if (completedProcessing)
            {
                lock (_analysisLock)
                {
                    foreach (var epoch in epochs)
                    {
                        _observedUrisByEpoch.Remove(epoch);
                    }
                }
            }

            epochTimer.Stop();

            // Record consolidated epoch metrics
            var totalItems = consolidatedStructureItems.Count;
            if (totalItems > 0)
            {
                Metrics?.EpochSize.Record(totalItems);
                Metrics?.EpochDuration.Record(epochTimer.Elapsed.TotalMilliseconds);
                Metrics?.EpochsCompleted.Add(epochs.Count); // Count all consolidated epochs

                // Record "ready" milestone once — the first time idle processing completes after startup
                var startTicks = Interlocked.Read(ref _firstEpochStartTicks);
                if (startTicks > 0 && Interlocked.Exchange(ref _readyLogged, 1) == 0)
                {
                    var totalElapsed = Stopwatch.GetElapsedTime(startTicks);
                    RecordMilestone("ready", $"{totalItems} files, {totalElapsed.TotalSeconds:F1}s");
                }
            }

            // Clear peak tracking for all processed epochs
            foreach (var epoch in epochs)
            {
                _epochTracker.ClearEpochPeak(epoch);
            }
        }
    }

    private (int StructureCount, int AnalysisCount, int EpochCount) RequeueIdleBacklogAfterFailure(
        IReadOnlyList<IndexItem> structureItems,
        IReadOnlyList<IndexItem> analysisItems,
        int analysisItemsAlreadyEnqueued)
    {
        // Don't requeue if either queue is terminally faulted — the items can never be processed.
        if (Volatile.Read(ref _indexerQueueFaulted) == 1 || Volatile.Read(ref _analysisQueueFaulted) == 1)
            return (0, 0, 0);

        var analysisStartIndex = Math.Clamp(analysisItemsAlreadyEnqueued, 0, analysisItems.Count);
        var requeuedEpochs = new HashSet<long>();
        var requeuedStructureCount = 0;
        var requeuedAnalysisCount = 0;

        lock (_analysisLock)
        {
            foreach (var item in structureItems)
            {
                if (!_pendingStructureEmbeddings.TryGetValue(item.Epoch, out var embedQueue))
                {
                    embedQueue = new Queue<IndexItem>();
                    _pendingStructureEmbeddings[item.Epoch] = embedQueue;
                }

                embedQueue.Enqueue(item);
                requeuedEpochs.Add(item.Epoch);
                requeuedStructureCount++;
            }

            for (var i = analysisStartIndex; i < analysisItems.Count; i++)
            {
                var item = analysisItems[i];
                if (!_pendingAnalysis.TryGetValue(item.Epoch, out var analysisQueue))
                {
                    analysisQueue = new Queue<IndexItem>();
                    _pendingAnalysis[item.Epoch] = analysisQueue;
                }

                analysisQueue.Enqueue(item);
                requeuedEpochs.Add(item.Epoch);
                requeuedAnalysisCount++;
            }
        }

        foreach (var epoch in requeuedEpochs)
        {
            EnqueueIdleEpoch(epoch);
        }

        return (requeuedStructureCount, requeuedAnalysisCount, requeuedEpochs.Count);
    }

    private void TrackObservedUri(long epoch, RepoUri uri)
    {
        if (epoch < 0)
            return;

        lock (_analysisLock)
        {
            if (!_observedUrisByEpoch.TryGetValue(epoch, out var observedUris))
            {
                observedUris = new HashSet<RepoUri>();
                _observedUrisByEpoch[epoch] = observedUris;
            }

            observedUris.Add(uri);
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
        item.SetCurrentOperation("classification");
        var classifyTimer = Stopwatch.StartNew();
        var pipelineResult = await RunHotPathStageAsync(item, _classificationStage, cancellationToken).ConfigureAwait(false);
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

        // Stage boundary: classification -> parsing.
        // Queue commands can flip status to Failed/Skipped while classification is running.
        if (ShouldAbortAtStageBoundary(item.Uri, out _))
            return PipelineResult.Filtered;

        // Lightweight detection: vendor/minified/sourcemap files get plain-text-only parsing
        item.IsLightweight = IndexItem.MatchesLightweightPattern(item.Uri.ToString());

        // Parsing stage
        item.SetCurrentOperation("parsing");
        var parseTimer = Stopwatch.StartNew();
        pipelineResult = await RunHotPathStageAsync(item, _parsingStage, cancellationToken).ConfigureAwait(false);
        parseTimer.Stop();
        RecordStageDuration("parsing", parseTimer.Elapsed.TotalMilliseconds, pipelineResult, item);
        Metrics?.FilesParsed.Add(1, new TagList
        {
            { "mime_type", mime },
            { "result", pipelineResult.ToString() }
        });
        if (pipelineResult != PipelineResult.Success)
            return pipelineResult;

        // Stage boundary: parsing -> analysis.
        if (ShouldAbortAtStageBoundary(item.Uri, out _))
            return PipelineResult.Filtered;

        if (item.IsReadOnly)
        {
            AddEpochTag(item.Epoch, "analysis.skip", "read_only_single");
            return PipelineResult.Success;
        }

        // Single-file analysis stage
        item.SetCurrentOperation("single_file_analysis");
        var analysisTimer = Stopwatch.StartNew();
        pipelineResult = await RunHotPathStageAsync(item, _singleFileStage, cancellationToken).ConfigureAwait(false);
        analysisTimer.Stop();
        RecordStageDuration("single_file_analysis", analysisTimer.Elapsed.TotalMilliseconds, pipelineResult, item);
        Metrics?.FilesEnriched.Add(1, new TagList
        {
            { "mime_type", mime },
            { "result", pipelineResult.ToString() }
        });

        return pipelineResult;
    }

    private static string BuildPipelineFailureMessage(IndexItem item, string currentStage, PipelineResult result)
    {
        if (!string.IsNullOrWhiteSpace(item.FailureDetail))
            return item.FailureDetail;

        var stage = string.IsNullOrWhiteSpace(item.CurrentOperation) || item.CurrentOperation == "pipeline"
            ? currentStage
            : item.CurrentOperation;

        return $"{stage}: returned {result}";
    }

    private bool ShouldAbortAtStageBoundary(RepoUri uri, out UriStatus status)
    {
        status = default;
        if (UriRegistry is null)
            return false;

        if (!UriRegistry.TryGetValue(uri, out var entry))
            return false;

        if (entry.Status != UriStatus.Failed && entry.Status != UriStatus.Skipped)
            return false;

        status = entry.Status;
        return true;
    }

    private async Task<PipelineResult> RunHotPathStageAsync(IndexItem item, StageContext stage, CancellationToken cancellationToken)
    {
        UpdateStateFlags(stage.BusyFlag, stage.IdleFlag, true);
        item.TrackHotPathStage(stage.BusyFlag, stage.IdleFlag);

        try
        {
            return await stage.Processor(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (item.TryClaimHotPathStageCleanup(out var busyFlag, out var idleFlag))
            {
                UpdateStateFlags(busyFlag, idleFlag, false);
            }

            item.ClearHotPathStageTracking();
        }
    }

    internal async Task AnalyzeItemAsync(IndexItem item, CancellationToken cancellationToken)
    {
        try
        {
            item.SetCurrentOperation(item.IsDeferredRetry ? "idle_retry_analysis" : "analysis");
            var multiFileTask = _multiFileStage.RunAsync(item, cancellationToken, UpdateStateFlags);
            var rebuildTask = _indexRebuildStage.RunAsync(item, cancellationToken, UpdateStateFlags);
            // Format-specific multi-file analyzers will be plugged into MultiFileAnalyzer; they remain parallel to rebuild.
            await Task.WhenAll(multiFileTask, rebuildTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!Shutdown.IsCancellationRequested)
        {
            throw;
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

        // All processing is complete — release Records to free the remaining graph data.
        item.ReleasePostIdlePayload();
        item.SetCurrentOperation(null);
    }

    private IndexItem CreateDeferredRetryItem(IndexItem item)
    {
        var retryItem = CreateRetryIndexItem(item, item.Options);
        retryItem.SetEpoch(item.Epoch);
        retryItem.MarkDeferredRetry();
        retryItem.IncrementTimeoutAttempts();
        retryItem.SetCurrentOperation(item.CurrentOperation);
        return retryItem;
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
        var stage = string.IsNullOrWhiteSpace(item.CurrentOperation) ? "hot_path" : item.CurrentOperation;

        // Record timeout in metrics
        Metrics?.FilesErrored.Add(1, new TagList
        {
            { "mime_type", mime },
            { "error_type", "TimeoutException" },
            { "stage", stage }
        });

        // Store last error for diagnostics unless the queue has already entered a terminal fault.
        if (Volatile.Read(ref _indexerQueueFaulted) == 0)
        {
            Volatile.Write(ref _lastError, $"{item.Uri}: Timed out in {stage} after {elapsed.TotalSeconds:F1}s");
        }

        item.TryMarkTimedOut();
        item.IncrementTimeoutAttempts();

        if (item.TryClaimHotPathStageCleanup(out var busyFlag, out var idleFlag))
        {
            UpdateStateFlags(busyFlag, idleFlag, false);
            item.ClearHotPathStageTracking();
        }

        // Ensure pending digest state is cleared even when the processing task never returns.
        // Without this, DocumentCatalog may keep a stale pending entry and skip future reindex attempts.
        DocumentCatalog.CompleteProcessing(item.Uri);

        // Add epoch tag for tracing
        AddEpochTag(item.Epoch, "index.result", "timeout");
        AddEpochTag(item.Epoch, "index.timeout_duration_ms", elapsed.TotalMilliseconds);
        AddEpochTag(item.Epoch, "index.timeout_stage", stage);

        QueueDeferredRetry(item, elapsed, stage);
        ReleaseEpochAfterHotPathTimeout(item);

        LogItemTimedOut(Logger, item.Uri, elapsed.TotalSeconds);
    }

    private void QueueDeferredRetry(IndexItem item, TimeSpan elapsed, string stage)
    {
        if (item.IsDeferredRetry)
        {
            UriRegistry?.SetFailed(item.Uri, $"idle retry timed out in {stage} after {elapsed.TotalSeconds:F1}s");
            return;
        }

        if (Volatile.Read(ref _deferredRetryQueueFaulted) == 1)
        {
            UriRegistry?.SetFailed(item.Uri, $"hot-path timeout in {stage} after {elapsed.TotalSeconds:F1}s (deferred retry unavailable)");
            return;
        }

        var retryItem = CreateDeferredRetryItem(item);
        _deferredRetryOwnership[GetQueueKey(item.Uri)] = 0;
        lock (_analysisLock)
        {
            _pendingDeferredHotPathRetries.Enqueue(retryItem);
        }

        Interlocked.Increment(ref _deferredToIdleCount);
        ScheduleDeferredRetryDrain();
    }

    private void HandleAnalysisItemTimeout(IndexItem item, TimeSpan elapsed)
    {
        if (Volatile.Read(ref _analysisQueueFaulted) == 0)
        {
            Volatile.Write(ref _lastError, $"Analysis {item.Uri}: Timed out after {elapsed.TotalSeconds:F1}s");
        }

        AddEpochTag(item.Epoch, "analysis.result", "timeout");
        AddEpochTag(item.Epoch, "analysis.timeout_duration_ms", elapsed.TotalMilliseconds);

        Logger.LogWarning(
            "Analysis item {Uri} timed out after {ElapsedSeconds:F1}s",
            item.Uri,
            elapsed.TotalSeconds);
    }

    private void HandleDeferredRetryItemTimeout(IndexItem item, TimeSpan elapsed)
    {
        if (Volatile.Read(ref _deferredRetryQueueFaulted) == 0)
        {
            Volatile.Write(ref _lastError, $"Deferred retry {item.Uri}: timed out after {elapsed.TotalSeconds:F1}s");
        }

        AddEpochTag(item.Epoch, "idle_retry.result", "timeout");
        AddEpochTag(item.Epoch, "idle_retry.timeout_duration_ms", elapsed.TotalMilliseconds);
        UriRegistry?.SetFailed(item.Uri, $"idle retry timed out after {elapsed.TotalSeconds:F1}s");
        _deferredRetryOwnership.TryRemove(GetQueueKey(item.Uri), out _);

        Logger.LogWarning(
            "Deferred retry item {Uri} timed out after {ElapsedSeconds:F1}s",
            item.Uri,
            elapsed.TotalSeconds);
    }

    private void ReleaseEpochAfterHotPathTimeout(IndexItem item)
    {
        // CRITICAL: Decrement epoch counter to prevent epoch imbalance (FM-003)
        // The item was incremented when enqueued, and normally decremented in IndexItemAsync's finally block.
        // Since IndexItemAsync was cancelled mid-processing, we must decrement here.
        if (item.TryMarkEpochComplete())
        {
            var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
            if (epochBecameIdle)
            {
                if (State == IndexingState.AllIdle)
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
                else
                {
                    EnqueueIdleEpoch(item.Epoch);
                }
            }
        }
    }

    private void HandleIndexerQueueFault(Exception ex)
    {
        Volatile.Write(ref _indexerQueueFaulted, 1);
        Volatile.Write(ref _lastError, $"IndexingQueue fault: {ex.Message}");
        Logger.LogCritical(ex, "Indexer queue entered a terminal fault.");

        // Clear pending idle processing state — no further idle work can run after a terminal fault.
        // Without this, items that completed the hot path before the fault remain orphaned in the
        // pending collections, causing GetPendingIdleProcessingCount() to report stale counts.
        lock (_analysisLock)
        {
            foreach (var pendingItem in _pendingDeferredHotPathRetries)
            {
                _deferredRetryOwnership.TryRemove(GetQueueKey(pendingItem.Uri), out _);
            }

            _pendingAnalysis.Clear();
            _pendingStructureEmbeddings.Clear();
            _pendingDeferredHotPathRetries.Clear();
        }
    }

    private void HandleAnalysisQueueFault(Exception ex)
    {
        Volatile.Write(ref _analysisQueueFaulted, 1);
        Volatile.Write(ref _lastError, $"AnalysisQueue fault: {ex.Message}");
        Logger.LogCritical(ex, "Analysis queue entered a terminal fault.");

        // Clear pending idle processing state — the analysis queue can no longer accept work,
        // and RequeueIdleBacklogAfterFailure would keep reviving doomed items otherwise.
        lock (_analysisLock)
        {
            _pendingAnalysis.Clear();
            _pendingStructureEmbeddings.Clear();
        }
    }

    private void HandleDeferredRetryQueueFault(Exception ex)
    {
        Volatile.Write(ref _deferredRetryQueueFaulted, 1);
        Volatile.Write(ref _lastError, $"DeferredRetryQueue fault: {ex.Message}");
        Logger.LogCritical(ex, "Deferred retry queue entered a terminal fault.");

        lock (_analysisLock)
        {
            foreach (var pendingItem in _pendingDeferredHotPathRetries)
            {
                _deferredRetryOwnership.TryRemove(GetQueueKey(pendingItem.Uri), out _);
            }

            _pendingDeferredHotPathRetries.Clear();
        }
    }

    private bool CanDispatchDeferredRetries()
    {
        var hotPathSnapshot = IndexerQueue.CaptureSnapshot();
        return hotPathSnapshot.Depth == 0
            && (State & HotPathBusyMask) == 0;
    }

    private void ScheduleDeferredRetryDrain()
    {
        if (Interlocked.Exchange(ref _deferredRetryWakeScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await IndexerQueue.WhenIdleAsync().WaitAsync(Shutdown.Token).ConfigureAwait(false);
                if (Shutdown.IsCancellationRequested)
                    return;

                if (!CanDispatchDeferredRetries())
                    return;

                IndexItem[] deferredItems;
                lock (_analysisLock)
                {
                    if (_pendingDeferredHotPathRetries.Count == 0)
                        return;

                    deferredItems = _pendingDeferredHotPathRetries.ToArray();
                    _pendingDeferredHotPathRetries.Clear();
                }

                using (ActivitySource.StartActivity("deferred_retry_phase", ActivityKind.Internal))
                {
                    var deferredTimer = Stopwatch.StartNew();
                    foreach (var item in deferredItems)
                    {
                        await DeferredRetryQueue.EnqueueAsync(item, Shutdown.Token).ConfigureAwait(false);
                    }

                    await DeferredRetryQueue.WhenIdleAsync().ConfigureAwait(false);
                    deferredTimer.Stop();
                    Metrics?.IdlePhaseDuration.Record(deferredTimer.Elapsed.TotalMilliseconds, new TagList
                    {
                        { "phase", "deferred_retry" }
                    });
                    RecordMilestone("deferred_retry", $"{deferredItems.Length} items, {deferredTimer.Elapsed.TotalMilliseconds:F1}ms");
                }
            }
            catch (OperationCanceledException) when (Shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _deferredRetryWakeScheduled, 0);
                lock (_analysisLock)
                {
                    if (_pendingDeferredHotPathRetries.Count > 0 && !Shutdown.IsCancellationRequested)
                    {
                        ScheduleDeferredRetryDrain();
                    }
                }
            }
        });
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

    /// <summary>
    /// Extracts symbol URIs with span data from parsed records for the URI registry.
    /// </summary>
    internal static IReadOnlyDictionary<RepoUri, SymbolEntry> ExtractSymbolsFromRecords(Records? records)
    {
        if (records is null)
            return new Dictionary<RepoUri, SymbolEntry>().AsReadOnly();

        // Build span lookup for efficient access
        var spanLookup = records.Spans.ToDictionary(s => s.Id);

        var symbols = new Dictionary<RepoUri, SymbolEntry>();
        foreach (var node in records.Nodes)
        {
            // Skip document nodes - we only want symbols (types, functions, etc.)
            if (string.Equals(node.Kind, "document", StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip nodes without a URI
            if (node.Uri is null)
                continue;

            // Look up span for this node
            int startLine = 0, endLine = 0;
            if (node.SpanId.HasValue && spanLookup.TryGetValue(node.SpanId.Value, out var span))
            {
                startLine = span.StartLine ?? 0;
                endLine = span.EndLine ?? 0;
            }

            symbols[node.Uri] = new SymbolEntry(node.Kind, startLine, endLine);
        }

        return symbols.AsReadOnly();
    }

    /// <summary>
    /// Extracts x-ray headline and structure from the primary artifact in records.
    /// </summary>
    internal static (string? Headline, string? Structure) ExtractXraySummaries(Records? records)
    {
        if (records is null || records.Artifacts.Length == 0)
            return (null, null);

        var artifact = records.Artifacts[0];
        return (artifact.Headline, artifact.Structure);
    }

    /// <summary>
    /// Extracts line count from the artifact text in records.
    /// </summary>
    internal static int ExtractLineCount(Records? records)
    {
        if (records is null || records.Artifacts.Length == 0)
            return 0;

        // Use the first artifact (primary document)
        var text = records.Artifacts[0].Text;
        if (string.IsNullOrEmpty(text))
            return 0;

        // Count newlines + 1 (a file with no newlines has 1 line)
        var lineCount = 1;
        foreach (var c in text)
        {
            if (c == '\n')
                lineCount++;
        }

        return lineCount;
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
            var xKey = RepoUri.NormalizeContainer(x.Uri);
            var yKey = RepoUri.NormalizeContainer(y.Uri);
            return StringComparer.OrdinalIgnoreCase.Equals(xKey, yKey);
        }

        public int GetHashCode(IndexItem obj)
        {
            if (obj is null)
                return 0;
            var key = RepoUri.NormalizeContainer(obj.Uri);
            return StringComparer.OrdinalIgnoreCase.GetHashCode(key);
        }
    }

    private sealed class EpochTracker
    {
        private long _currentEpoch;
        private readonly Dictionary<long, int> _pendingByEpoch = new();
        private readonly Dictionary<long, int> _peakByEpoch = new();
        private readonly Dictionary<long, long> _epochStartTicks = new();
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

                // Record epoch start time on first item
                if (count == 0)
                    _epochStartTicks[epoch] = Stopwatch.GetTimestamp();

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
        /// Gets the elapsed time since the first item was enqueued for this epoch.
        /// </summary>
        public TimeSpan GetEpochElapsed(long epoch)
        {
            lock (_lock)
            {
                return _epochStartTicks.TryGetValue(epoch, out var startTicks)
                    ? Stopwatch.GetElapsedTime(startTicks)
                    : TimeSpan.Zero;
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
                _epochStartTicks.Remove(epoch);
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

    [LoggerMessage(LogLevel.Warning, "Slow operation detected: {Operation} took {ElapsedSeconds:F1}s for {Uri} (threshold={ThresholdSeconds:F0}s). Consider investigating if this pattern repeats.")]
    static partial void LogSlowOperation(ILogger<IndexingEngine> logger, string operation, RepoUri uri, double elapsedSeconds, double thresholdSeconds);

    [LoggerMessage(LogLevel.Information, "{Uri} was deleted before indexing completed; marking as pruned.")]
    static partial void LogFileDeletedBeforeIndexing(ILogger<IndexingEngine> logger, RepoUri uri);
    #endregion
}

