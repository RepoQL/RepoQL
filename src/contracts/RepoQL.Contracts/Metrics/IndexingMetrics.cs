using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RepoQL.Metrics;

/// <summary>
/// Centralized metrics for RepoQL indexing engine using OpenTelemetry.
/// </summary>
/// <remarks>
/// <para><strong>Design Principles:</strong></para>
/// <list type="bullet">
/// <item><description>Measure at boundaries - instrument at stage transitions</description></item>
/// <item><description>Tag richly - include mime_type, status, result for drill-down</description></item>
/// <item><description>Minimize state - let OpenTelemetry calculate rates from monotonic counters</description></item>
/// <item><description>Multi-measurement pattern - single callback for related gauges</description></item>
/// </list>
/// </remarks>
public sealed class IndexingMetrics : IDisposable
{
    private readonly Meter _meter;

    #region Counter Metrics (21 metrics)

    /// <summary>Files added to indexing queue.</summary>
    public Counter<long> FilesEnqueued { get; }

    /// <summary>Files rejected by filters (.gitignore, size).</summary>
    public Counter<long> FilesFiltered { get; }

    /// <summary>Files skipped (up-to-date, no changes).</summary>
    public Counter<long> FilesSkipped { get; }

    /// <summary>Files successfully classified.</summary>
    public Counter<long> FilesClassified { get; }

    /// <summary>Files successfully parsed.</summary>
    public Counter<long> FilesParsed { get; }

    /// <summary>Files analyzed (single-file).</summary>
    public Counter<long> FilesEnriched { get; }

    /// <summary>Files committed to database.</summary>
    public Counter<long> FilesIndexed { get; }

    /// <summary>Files that failed processing.</summary>
    public Counter<long> FilesErrored { get; }

    /// <summary>Files identified as deleted during prune.</summary>
    public Counter<long> FilesPruned { get; }

    /// <summary>New documents inserted.</summary>
    public Counter<long> DocumentsCreated { get; }

    /// <summary>Existing documents updated.</summary>
    public Counter<long> DocumentsUpdated { get; }

    /// <summary>Documents removed from index.</summary>
    public Counter<long> DocumentsDeleted { get; }

    /// <summary>Graph nodes extracted from documents.</summary>
    public Counter<long> NodesExtracted { get; }

    /// <summary>Graph edges extracted from documents.</summary>
    public Counter<long> EdgesExtracted { get; }

    /// <summary>Source location spans extracted.</summary>
    public Counter<long> SpansExtracted { get; }

    /// <summary>Annotations added/updated.</summary>
    public Counter<long> AnnotationsUpserted { get; }

    /// <summary>Indexing epochs completed.</summary>
    public Counter<long> EpochsCompleted { get; }

    /// <summary>Idle processing cycles triggered.</summary>
    public Counter<long> IdleCycles { get; }

    /// <summary>Database transactions committed.</summary>
    public Counter<long> TransactionsCommitted { get; }

    /// <summary>Database transactions failed.</summary>
    public Counter<long> TransactionsFailed { get; }

    /// <summary>Writer batches committed.</summary>
    public Counter<long> BatchesCommitted { get; }

    /// <summary>Total bytes processed.</summary>
    public Counter<long> BytesProcessed { get; }

    #endregion

    #region Histogram Metrics (9 metrics)

    /// <summary>Time spent in each pipeline stage.</summary>
    public Histogram<double> StageDuration { get; }

    /// <summary>Total time for file through hot path.</summary>
    public Histogram<double> HotPathDuration { get; }

    /// <summary>Database write operation duration.</summary>
    public Histogram<double> DbWriteDuration { get; }

    /// <summary>Batch transaction duration.</summary>
    public Histogram<double> BatchDuration { get; }

    /// <summary>Time spent waiting in writer queue.</summary>
    public Histogram<double> QueueWaitTime { get; }

    /// <summary>Total items processed in epoch.</summary>
    public Histogram<int> EpochSize { get; }

    /// <summary>Total epoch idle processing duration.</summary>
    public Histogram<double> EpochDuration { get; }

    /// <summary>Duration of idle processing phase.</summary>
    public Histogram<double> IdlePhaseDuration { get; }

    /// <summary>Size of processed files.</summary>
    public Histogram<long> FileSize { get; }

    #endregion

    #region Embedding Metrics

    /// <summary>Number of embedding requests (query-time or refresh).</summary>
    public Counter<long> EmbedRequests { get; }

    /// <summary>Number of embedding failures.</summary>
    public Counter<long> EmbedErrors { get; }

    /// <summary>Embedding computation duration.</summary>
    public Histogram<double> EmbedDuration { get; }

    /// <summary>Embedding phase durations (tokenize, tensor_prep, inference, postprocess, db, total).</summary>
    public Histogram<double> EmbeddingPhaseDuration { get; }

    /// <summary>Embedding batch sizes.</summary>
    public Histogram<int> EmbeddingBatchSize { get; }

    #endregion

    #region Observable Gauges

    /// <summary>Current items in queue by queue type.</summary>
    public ObservableGauge<int> QueueDepth { get; }

    /// <summary>Maximum queue capacity by queue type.</summary>
    public ObservableGauge<int> QueueCapacity { get; }

    /// <summary>Workers currently processing by queue type.</summary>
    public ObservableGauge<int> WorkersActive { get; }

    /// <summary>Total entries in document catalog.</summary>
    public ObservableGauge<int> CatalogEntries { get; }

    /// <summary>Pending digest computations.</summary>
    public ObservableGauge<int> CatalogPending { get; }

    /// <summary>Current epoch number.</summary>
    public ObservableGauge<long> EpochCurrent { get; }

    /// <summary>Pending items in current epoch.</summary>
    public ObservableGauge<int> EpochPendingItems { get; }

    /// <summary>Total entities in database by type.</summary>
    public ObservableGauge<long> DbTotals { get; }

    /// <summary>Active database connections.</summary>
    public ObservableGauge<int> DbConnectionsActive { get; }

    #endregion

    #region Callback Fields

    // Queue callbacks
    private Func<int>? _indexerQueueDepthCallback;
    private Func<int>? _analysisQueueDepthCallback;
    private Func<int>? _writerQueueDepthCallback;
    private Func<int>? _indexerQueueCapacityCallback;
    private Func<int>? _analysisQueueCapacityCallback;
    private Func<int>? _writerQueueCapacityCallback;
    private Func<int>? _indexerWorkersActiveCallback;
    private Func<int>? _analysisWorkersActiveCallback;

    // Catalog callbacks
    private Func<int>? _catalogEntriesCallback;
    private Func<int>? _catalogPendingCallback;

    // Epoch callbacks
    private Func<long>? _epochCurrentCallback;
    private Func<int>? _epochPendingCallback;

    // Database callbacks
    private Func<long>? _dbDocumentsTotalCallback;
    private Func<long>? _dbNodesTotalCallback;
    private Func<long>? _dbEdgesTotalCallback;
    private Func<long>? _dbAnnotationsTotalCallback;
    private Func<long>? _dbEmbeddingsTotalCallback;
    private Func<int>? _dbConnectionsActiveCallback;

    #endregion

    public IndexingMetrics(string meterName = "RepoQL.Indexing", string? version = "1.0.0")
    {
        _meter = new Meter(meterName, version);

        #region Initialize Counters

        FilesEnqueued = _meter.CreateCounter<long>(
            "repoql.files.enqueued",
            unit: "files",
            description: "Files added to indexing queue");

        FilesFiltered = _meter.CreateCounter<long>(
            "repoql.files.filtered",
            unit: "files",
            description: "Files rejected by filters (.gitignore, size)");

        FilesSkipped = _meter.CreateCounter<long>(
            "repoql.files.skipped",
            unit: "files",
            description: "Files skipped (up-to-date, no changes)");

        FilesClassified = _meter.CreateCounter<long>(
            "repoql.files.classified",
            unit: "files",
            description: "Files successfully classified");

        FilesParsed = _meter.CreateCounter<long>(
            "repoql.files.parsed",
            unit: "files",
            description: "Files successfully parsed");

        FilesEnriched = _meter.CreateCounter<long>(
            "repoql.files.enriched",
            unit: "files",
            description: "Files analyzed (single-file)");

        FilesIndexed = _meter.CreateCounter<long>(
            "repoql.files.indexed",
            unit: "files",
            description: "Files committed to database");

        FilesErrored = _meter.CreateCounter<long>(
            "repoql.files.errored",
            unit: "files",
            description: "Files that failed processing");

        FilesPruned = _meter.CreateCounter<long>(
            "repoql.files.pruned",
            unit: "files",
            description: "Files identified as deleted during prune");

        DocumentsCreated = _meter.CreateCounter<long>(
            "repoql.documents.created",
            unit: "documents",
            description: "New documents inserted");

        DocumentsUpdated = _meter.CreateCounter<long>(
            "repoql.documents.updated",
            unit: "documents",
            description: "Existing documents updated");

        DocumentsDeleted = _meter.CreateCounter<long>(
            "repoql.documents.deleted",
            unit: "documents",
            description: "Documents removed from index");

        NodesExtracted = _meter.CreateCounter<long>(
            "repoql.nodes.extracted",
            unit: "nodes",
            description: "Graph nodes extracted from documents");

        EdgesExtracted = _meter.CreateCounter<long>(
            "repoql.edges.extracted",
            unit: "edges",
            description: "Graph edges extracted from documents");

        SpansExtracted = _meter.CreateCounter<long>(
            "repoql.spans.extracted",
            unit: "spans",
            description: "Source location spans extracted");

        AnnotationsUpserted = _meter.CreateCounter<long>(
            "repoql.annotations.upserted",
            unit: "annotations",
            description: "Annotations added/updated");

        EpochsCompleted = _meter.CreateCounter<long>(
            "repoql.epochs.completed",
            unit: "epochs",
            description: "Indexing epochs completed");

        IdleCycles = _meter.CreateCounter<long>(
            "repoql.idle.cycles",
            unit: "cycles",
            description: "Idle processing cycles triggered");

        TransactionsCommitted = _meter.CreateCounter<long>(
            "repoql.transactions.committed",
            unit: "transactions",
            description: "Database transactions committed");

        TransactionsFailed = _meter.CreateCounter<long>(
            "repoql.transactions.failed",
            unit: "transactions",
            description: "Database transactions failed");

        BatchesCommitted = _meter.CreateCounter<long>(
            "repoql.batches.committed",
            unit: "batches",
            description: "Writer batches committed");

        BytesProcessed = _meter.CreateCounter<long>(
            "repoql.bytes.processed",
            unit: "bytes",
            description: "Total bytes processed");

        #endregion

        #region Initialize Histograms

        StageDuration = _meter.CreateHistogram<double>(
            "repoql.stage.duration",
            unit: "ms",
            description: "Time spent in each pipeline stage");

        HotPathDuration = _meter.CreateHistogram<double>(
            "repoql.hotpath.duration",
            unit: "ms",
            description: "Total time for file through hot path");

        DbWriteDuration = _meter.CreateHistogram<double>(
            "repoql.db.write.duration",
            unit: "ms",
            description: "Database write operation duration");

        BatchDuration = _meter.CreateHistogram<double>(
            "repoql.batch.duration",
            unit: "ms",
            description: "Batch transaction duration");

        QueueWaitTime = _meter.CreateHistogram<double>(
            "repoql.queue.wait_time",
            unit: "ms",
            description: "Time spent waiting in writer queue");

        EpochSize = _meter.CreateHistogram<int>(
            "repoql.epoch.size",
            unit: "items",
            description: "Total items processed in epoch");

        EpochDuration = _meter.CreateHistogram<double>(
            "repoql.epoch.duration",
            unit: "ms",
            description: "Total epoch idle processing duration");

        IdlePhaseDuration = _meter.CreateHistogram<double>(
            "repoql.idle.phase.duration",
            unit: "ms",
            description: "Duration of idle processing phase");

        FileSize = _meter.CreateHistogram<long>(
            "repoql.file.size",
            unit: "bytes",
            description: "Size of processed files");

        #endregion

        #region Initialize Embedding Metrics

        EmbedRequests = _meter.CreateCounter<long>(
            "repoql.embed.requests",
            unit: "calls",
            description: "Number of embedding requests (query-time or refresh)");

        EmbedErrors = _meter.CreateCounter<long>(
            "repoql.embed.errors",
            unit: "errors",
            description: "Number of embedding failures");

        EmbedDuration = _meter.CreateHistogram<double>(
            "repoql.embed.duration",
            unit: "ms",
            description: "Embedding computation duration");

        EmbeddingPhaseDuration = _meter.CreateHistogram<double>(
            "repoql.embed.phase.duration",
            unit: "ms",
            description: "Embedding phase durations (tokenize, tensor_prep, inference, postprocess, db, total)");

        EmbeddingBatchSize = _meter.CreateHistogram<int>(
            "repoql.embed.batch.size",
            unit: "items",
            description: "Embedding batch sizes");

        #endregion

        #region Initialize Observable Gauges (Multi-Measurement Pattern)

        QueueDepth = _meter.CreateObservableGauge(
            "repoql.queue.depth",
            observeValues: () => new[]
            {
                new Measurement<int>(_indexerQueueDepthCallback?.Invoke() ?? 0, new TagList { { "queue", "indexer" } }),
                new Measurement<int>(_analysisQueueDepthCallback?.Invoke() ?? 0, new TagList { { "queue", "analysis" } }),
                new Measurement<int>(_writerQueueDepthCallback?.Invoke() ?? 0, new TagList { { "queue", "writer" } })
            },
            unit: "items",
            description: "Current items in queue");

        QueueCapacity = _meter.CreateObservableGauge(
            "repoql.queue.capacity",
            observeValues: () => new[]
            {
                new Measurement<int>(_indexerQueueCapacityCallback?.Invoke() ?? 0, new TagList { { "queue", "indexer" } }),
                new Measurement<int>(_analysisQueueCapacityCallback?.Invoke() ?? 0, new TagList { { "queue", "analysis" } }),
                new Measurement<int>(_writerQueueCapacityCallback?.Invoke() ?? 0, new TagList { { "queue", "writer" } })
            },
            unit: "items",
            description: "Maximum queue capacity");

        WorkersActive = _meter.CreateObservableGauge(
            "repoql.workers.active",
            observeValues: () => new[]
            {
                new Measurement<int>(_indexerWorkersActiveCallback?.Invoke() ?? 0, new TagList { { "queue", "indexer" } }),
                new Measurement<int>(_analysisWorkersActiveCallback?.Invoke() ?? 0, new TagList { { "queue", "analysis" } })
            },
            unit: "workers",
            description: "Workers currently processing items");

        CatalogEntries = _meter.CreateObservableGauge(
            "repoql.catalog.entries",
            () => _catalogEntriesCallback?.Invoke() ?? 0,
            unit: "entries",
            description: "Total entries in document catalog");

        CatalogPending = _meter.CreateObservableGauge(
            "repoql.catalog.pending",
            () => _catalogPendingCallback?.Invoke() ?? 0,
            unit: "entries",
            description: "Pending digest computations");

        EpochCurrent = _meter.CreateObservableGauge(
            "repoql.epoch.current",
            () => _epochCurrentCallback?.Invoke() ?? 0,
            description: "Current epoch number");

        EpochPendingItems = _meter.CreateObservableGauge(
            "repoql.epoch.pending_items",
            () => _epochPendingCallback?.Invoke() ?? 0,
            unit: "items",
            description: "Pending items in current epoch");

        DbTotals = _meter.CreateObservableGauge(
            "repoql.db.total",
            observeValues: () => new[]
            {
                new Measurement<long>(_dbDocumentsTotalCallback?.Invoke() ?? 0, new TagList { { "entity_type", "documents" } }),
                new Measurement<long>(_dbNodesTotalCallback?.Invoke() ?? 0, new TagList { { "entity_type", "nodes" } }),
                new Measurement<long>(_dbEdgesTotalCallback?.Invoke() ?? 0, new TagList { { "entity_type", "edges" } }),
                new Measurement<long>(_dbAnnotationsTotalCallback?.Invoke() ?? 0, new TagList { { "entity_type", "annotations" } }),
                new Measurement<long>(_dbEmbeddingsTotalCallback?.Invoke() ?? 0, new TagList { { "entity_type", "embeddings" } })
            },
            description: "Total entities in database by type");

        DbConnectionsActive = _meter.CreateObservableGauge(
            "repoql.db.connections.active",
            () => _dbConnectionsActiveCallback?.Invoke() ?? 0,
            unit: "connections",
            description: "Active database connections");

        #endregion
    }

    #region Callback Registration Methods

    /// <summary>
    /// Registers callbacks for queue-related observable gauges.
    /// </summary>
    public void RegisterQueueCallbacks(
        Func<int> indexerDepth,
        Func<int> analysisDepth,
        Func<int> writerDepth,
        Func<int> indexerCapacity,
        Func<int> analysisCapacity,
        Func<int> writerCapacity,
        Func<int> indexerWorkers,
        Func<int> analysisWorkers)
    {
        _indexerQueueDepthCallback = indexerDepth;
        _analysisQueueDepthCallback = analysisDepth;
        _writerQueueDepthCallback = writerDepth;
        _indexerQueueCapacityCallback = indexerCapacity;
        _analysisQueueCapacityCallback = analysisCapacity;
        _writerQueueCapacityCallback = writerCapacity;
        _indexerWorkersActiveCallback = indexerWorkers;
        _analysisWorkersActiveCallback = analysisWorkers;
    }

    /// <summary>
    /// Registers callbacks for document catalog observable gauges.
    /// </summary>
    public void RegisterCatalogCallbacks(
        Func<int> entryCount,
        Func<int> pendingCount)
    {
        _catalogEntriesCallback = entryCount;
        _catalogPendingCallback = pendingCount;
    }

    /// <summary>
    /// Registers callbacks for epoch tracking observable gauges.
    /// </summary>
    public void RegisterEpochCallbacks(
        Func<long> currentEpoch,
        Func<int> pendingItems)
    {
        _epochCurrentCallback = currentEpoch;
        _epochPendingCallback = pendingItems;
    }

    /// <summary>
    /// Registers callbacks for database total observable gauges.
    /// </summary>
    public void RegisterDatabaseCallbacks(
        Func<long> documentsTotal,
        Func<long> nodesTotal,
        Func<long> edgesTotal,
        Func<long> annotationsTotal,
        Func<long> embeddingsTotal)
    {
        _dbDocumentsTotalCallback = documentsTotal;
        _dbNodesTotalCallback = nodesTotal;
        _dbEdgesTotalCallback = edgesTotal;
        _dbAnnotationsTotalCallback = annotationsTotal;
        _dbEmbeddingsTotalCallback = embeddingsTotal;
    }

    /// <summary>
    /// Sets callback for queue depth (legacy single-queue support).
    /// </summary>
    public void SetQueueDepthCallback(Func<int> callback) => _writerQueueDepthCallback = callback;

    /// <summary>
    /// Sets callback for queue capacity (legacy single-queue support).
    /// </summary>
    public void SetQueueCapacityCallback(Func<int> callback) => _writerQueueCapacityCallback = callback;

    /// <summary>
    /// Sets callback for active database connections.
    /// </summary>
    public void SetDbConnectionsActiveCallback(Func<int> callback) => _dbConnectionsActiveCallback = callback;

    #endregion

    #region Helper Methods

    /// <summary>
    /// Records a file processed through the hot path with all relevant metrics.
    /// </summary>
    public void RecordFileProcessed(string mimeType, string status, long fileSize)
    {
        var tags = new TagList
        {
            { "mime_type", mimeType },
            { "status", status }
        };

        FileSize.Record(fileSize, tags);
        BytesProcessed.Add(fileSize);
    }

    /// <summary>
    /// Records an error with categorization.
    /// </summary>
    public void RecordError(string errorType, string operation, string? stage = null)
    {
        var tags = new TagList
        {
            { "error_type", TruncateErrorType(errorType) },
            { "operation", operation }
        };
        if (stage is not null)
        {
            tags.Add("stage", stage);
        }

        FilesErrored.Add(1, tags);
    }

    /// <summary>
    /// Records a database write duration.
    /// </summary>
    public void RecordDbWriteDuration(double durationMs, string operation)
    {
        DbWriteDuration.Record(durationMs, new TagList { { "operation", operation } });
    }

    /// <summary>
    /// Records a committed transaction.
    /// </summary>
    public void RecordTransaction(string status)
    {
        if (status == "success")
        {
            TransactionsCommitted.Add(1);
        }
        else
        {
            TransactionsFailed.Add(1);
        }
    }

    /// <summary>
    /// Limits error type cardinality to prevent unbounded growth.
    /// </summary>
    private static string TruncateErrorType(string errorType)
    {
        // Known error types that should be tracked individually
        var knownErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IOException",
            "UnauthorizedAccessException",
            "FileNotFoundException",
            "DirectoryNotFoundException",
            "DuckDBException",
            "ArgumentException",
            "ArgumentNullException",
            "InvalidOperationException",
            "TimeoutException",
            "OperationCanceledException",
            "TaskCanceledException",
            "OutOfMemoryException",
            "NullReferenceException",
            "IndexOutOfRangeException",
            "FormatException",
            "JsonException",
            "XmlException",
            "NotSupportedException",
            "NotImplementedException",
            "ObjectDisposedException"
        };

        return knownErrors.Contains(errorType) ? errorType : "other";
    }

    #endregion

    public void Dispose()
    {
        _meter?.Dispose();
    }
}
