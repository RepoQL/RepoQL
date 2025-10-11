using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RepoQL.Core.Metrics;

/// <summary>
/// Centralized metrics for RepoQL indexing engine using OpenTelemetry
/// </summary>
public sealed class IndexingMetrics : IDisposable
{
    private readonly Meter _meter;

    // Counter metrics
    public Counter<long> FilesProcessed { get; }
    public Counter<long> StageDiscover { get; }
    public Counter<long> StageHash { get; }
    public Counter<long> StageParse { get; }
    public Counter<long> StageIndex { get; }
    public Counter<long> StageEnrich { get; }
    public Counter<long> FileSystemEvents { get; }
    public Counter<long> DocumentsCreated { get; }
    public Counter<long> DocumentsUpdated { get; }
    public Counter<long> NodesExtracted { get; }
    public Counter<long> EntitiesCreated { get; }
    public Counter<long> OccurrencesCreated { get; }
    public Counter<long> ErrorsTotal { get; }
    public Counter<long> TransactionsTotal { get; }
    public Counter<long> BytesProcessed { get; }

    // Histogram metrics
    public Histogram<double> ProcessingDuration { get; }
    public Histogram<double> ParseDuration { get; }
    public Histogram<double> EnrichmentDuration { get; }
    public Histogram<double> DbWriteDuration { get; }
    public Histogram<long> FileSize { get; }
    public Histogram<int> NodesPerDocument { get; }
    public Histogram<double> QueueWaitTime { get; }

    // Embedding metrics
    public Counter<long> EmbedRequests { get; }
    public Counter<long> EmbedErrors { get; }
    public Histogram<double> EmbedDuration { get; }

    // Gauge metrics (using ObservableGauge)
    public ObservableGauge<int> QueueDepth { get; }
    public ObservableGauge<int> QueueCapacity { get; }
    public ObservableGauge<int> WorkersActive { get; }
    public ObservableGauge<long> MemoryUsage { get; }
    public ObservableGauge<int> DbConnectionsActive { get; }
    public ObservableGauge<long> IndexSize { get; }
    public ObservableGauge<long> DocumentsTotal { get; }

    // Calculated rate metrics
    public ObservableGauge<double> ThroughputFilesPerSecond { get; }
    public ObservableGauge<double> ThroughputBytesPerSecond { get; }

    // Tracking for rate calculations
    private long _lastFilesProcessed;
    private long _lastBytesProcessed;
    private DateTime _lastMeasurement = DateTime.UtcNow;
    private readonly object _rateLock = new();

    // Callbacks for observable gauges
    private Func<int>? _queueDepthCallback;
    private Func<int>? _queueCapacityCallback;
    private Func<int>? _workersActiveCallback;
    private Func<long>? _memoryUsageCallback;
    private Func<int>? _dbConnectionsActiveCallback;
    private Func<long>? _indexSizeCallback;
    private Func<long>? _documentsTotalCallback;

    public IndexingMetrics(string meterName = "RepoQL.Indexing", string? version = "1.0.0")
    {
        _meter = new Meter(meterName, version);

        // Initialize counter metrics
        FilesProcessed = _meter.CreateCounter<long>(
            "repoql.files.processed",
            unit: "files",
            description: "Total number of files processed");

        StageDiscover = _meter.CreateCounter<long>(
            "repoql.stage.discover",
            unit: "files",
            description: "Files discovered for indexing");

        StageHash = _meter.CreateCounter<long>(
            "repoql.stage.hash",
            unit: "files",
            description: "Files hashed");

        StageParse = _meter.CreateCounter<long>(
            "repoql.stage.parse",
            unit: "files",
            description: "Files parsed");

        StageIndex = _meter.CreateCounter<long>(
            "repoql.stage.index",
            unit: "files",
            description: "Files indexed (written to DB or skipped up-to-date)");

        StageEnrich = _meter.CreateCounter<long>(
            "repoql.stage.enrich",
            unit: "files",
            description: "Documents enriched");

        FileSystemEvents = _meter.CreateCounter<long>(
            "repoql.fs.events",
            unit: "events",
            description: "File system change events");

        DocumentsCreated = _meter.CreateCounter<long>(
            "repoql.documents.created",
            unit: "documents",
            description: "Number of new documents created");

        DocumentsUpdated = _meter.CreateCounter<long>(
            "repoql.documents.updated",
            unit: "documents",
            description: "Number of existing documents updated");

        NodesExtracted = _meter.CreateCounter<long>(
            "repoql.nodes.extracted",
            unit: "nodes",
            description: "Total number of nodes extracted from documents");

        EntitiesCreated = _meter.CreateCounter<long>(
            "repoql.entities.created",
            unit: "entities",
            description: "Number of entities discovered and created");

        OccurrencesCreated = _meter.CreateCounter<long>(
            "repoql.occurrences.created",
            unit: "occurrences",
            description: "Number of entity occurrences found");

        ErrorsTotal = _meter.CreateCounter<long>(
            "repoql.errors.total",
            unit: "errors",
            description: "Total number of errors encountered");

        TransactionsTotal = _meter.CreateCounter<long>(
            "repoql.transactions.total",
            unit: "transactions",
            description: "Total number of database transactions");

        BytesProcessed = _meter.CreateCounter<long>(
            "repoql.bytes.processed",
            unit: "bytes",
            description: "Total bytes of data processed");

        // Initialize histogram metrics
        ProcessingDuration = _meter.CreateHistogram<double>(
            "repoql.processing.duration",
            unit: "ms",
            description: "Time to process each file in milliseconds");

        ParseDuration = _meter.CreateHistogram<double>(
            "repoql.parse.duration",
            unit: "ms",
            description: "Time spent parsing files in milliseconds");

        EnrichmentDuration = _meter.CreateHistogram<double>(
            "repoql.enrichment.duration",
            unit: "ms",
            description: "Time spent in enrichers in milliseconds");

        DbWriteDuration = _meter.CreateHistogram<double>(
            "repoql.db.write.duration",
            unit: "ms",
            description: "Database write time per document in milliseconds");

        FileSize = _meter.CreateHistogram<long>(
            "repoql.file.size",
            unit: "bytes",
            description: "Size of processed files in bytes");

        NodesPerDocument = _meter.CreateHistogram<int>(
            "repoql.nodes.per_document",
            unit: "nodes",
            description: "Number of nodes extracted per document");

        QueueWaitTime = _meter.CreateHistogram<double>(
            "repoql.queue.wait_time",
            unit: "ms",
            description: "Time items spend in queue before processing in milliseconds");

        // Embeddings
        EmbedRequests = _meter.CreateCounter<long>(
            "repoql.embed.requests",
            unit: "calls",
            description: "Number of embedding requests (query-time or refresh)"
        );

        EmbedErrors = _meter.CreateCounter<long>(
            "repoql.embed.errors",
            unit: "errors",
            description: "Number of embedding failures"
        );

        EmbedDuration = _meter.CreateHistogram<double>(
            "repoql.embed.duration",
            unit: "ms",
            description: "Embedding computation duration"
        );

        // Initialize observable gauge metrics
        QueueDepth = _meter.CreateObservableGauge(
            "repoql.queue.depth",
            () => _queueDepthCallback?.Invoke() ?? 0,
            unit: "items",
            description: "Current queue size");

        QueueCapacity = _meter.CreateObservableGauge(
            "repoql.queue.capacity",
            () => _queueCapacityCallback?.Invoke() ?? 0,
            unit: "items",
            description: "Maximum queue capacity");

        WorkersActive = _meter.CreateObservableGauge(
            "repoql.workers.active",
            () => _workersActiveCallback?.Invoke() ?? 0,
            unit: "workers",
            description: "Number of active worker threads");

        MemoryUsage = _meter.CreateObservableGauge(
            "repoql.memory.usage",
            () => _memoryUsageCallback?.Invoke() ?? GC.GetTotalMemory(false),
            unit: "bytes",
            description: "Memory usage of indexing process");

        DbConnectionsActive = _meter.CreateObservableGauge(
            "repoql.db.connections.active",
            () => _dbConnectionsActiveCallback?.Invoke() ?? 0,
            unit: "connections",
            description: "Active database connections");

        IndexSize = _meter.CreateObservableGauge(
            "repoql.index.size",
            () => _indexSizeCallback?.Invoke() ?? 0,
            unit: "bytes",
            description: "Total size of index in bytes");

        DocumentsTotal = _meter.CreateObservableGauge(
            "repoql.documents.total",
            () => _documentsTotalCallback?.Invoke() ?? 0,
            unit: "documents",
            description: "Current total documents in index");

        // Rate metrics
        ThroughputFilesPerSecond = _meter.CreateObservableGauge(
            "repoql.throughput.files_per_second",
            CalculateFilesPerSecond,
            unit: "files/s",
            description: "Current file processing rate");

        ThroughputBytesPerSecond = _meter.CreateObservableGauge(
            "repoql.throughput.bytes_per_second",
            CalculateBytesPerSecond,
            unit: "bytes/s",
            description: "Current data ingestion rate");
    }

    // Configuration methods for observable gauges
    public void SetQueueDepthCallback(Func<int> callback) => _queueDepthCallback = callback;
    public void SetQueueCapacityCallback(Func<int> callback) => _queueCapacityCallback = callback;
    public void SetWorkersActiveCallback(Func<int> callback) => _workersActiveCallback = callback;
    public void SetMemoryUsageCallback(Func<long> callback) => _memoryUsageCallback = callback;
    public void SetDbConnectionsActiveCallback(Func<int> callback) => _dbConnectionsActiveCallback = callback;
    public void SetIndexSizeCallback(Func<long> callback) => _indexSizeCallback = callback;
    public void SetDocumentsTotalCallback(Func<long> callback) => _documentsTotalCallback = callback;

    // Helper methods for recording metrics with tags
    public void RecordFileProcessed(string mimeType, string status, long fileSize, double durationMs)
    {
        var tags = new TagList
        {
            { "mime_type", mimeType },
            { "status", status }
        };

        FilesProcessed.Add(1, tags);
        FileSize.Record(fileSize, tags);
        ProcessingDuration.Record(durationMs, tags);
        BytesProcessed.Add(fileSize);

        lock (_rateLock)
        {
            _lastFilesProcessed++;
            _lastBytesProcessed += fileSize;
        }
    }

    public void RecordError(string errorType, string operation)
    {
        ErrorsTotal.Add(1, new TagList
        {
            { "error_type", errorType },
            { "operation", operation }
        });
    }

    // Stage helpers
    public void IncrementDiscover() => StageDiscover.Add(1);
    public void IncrementHash() => StageHash.Add(1);
    public void IncrementParse() => StageParse.Add(1);
    public void IncrementIndex() => StageIndex.Add(1);
    public void IncrementEnrich() => StageEnrich.Add(1);
    public void RecordFsEvent(string kind) => FileSystemEvents.Add(1, new TagList { { "kind", kind } });

    public void RecordTransaction(string status)
    {
        TransactionsTotal.Add(1, new TagList { { "status", status } });
    }

    public void RecordParseDuration(double durationMs, string parser, string mimeType)
    {
        ParseDuration.Record(durationMs, new TagList
        {
            { "parser", parser },
            { "mime_type", mimeType }
        });
    }

    public void RecordEnrichmentDuration(double durationMs, string enricher)
    {
        EnrichmentDuration.Record(durationMs, new TagList { { "enricher", enricher } });
    }

    public void RecordDbWriteDuration(double durationMs, string operation)
    {
        DbWriteDuration.Record(durationMs, new TagList { { "operation", operation } });
    }

    private double CalculateFilesPerSecond()
    {
        lock (_rateLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastMeasurement).TotalSeconds;

            if (elapsed <= 0) return 0;

            var rate = _lastFilesProcessed / elapsed;

            // Reset for next measurement
            _lastFilesProcessed = 0;
            _lastMeasurement = now;

            return rate;
        }
    }

    private double CalculateBytesPerSecond()
    {
        lock (_rateLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastMeasurement).TotalSeconds;

            if (elapsed <= 0) return 0;

            var rate = _lastBytesProcessed / elapsed;

            // Reset for next measurement
            _lastBytesProcessed = 0;

            return rate;
        }
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}
