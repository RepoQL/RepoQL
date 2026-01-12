# RepoQL Metrics Instrumentation Design

**Version**: 2.0 (Final)
**Date**: 2025-01-24
**Status**: Ready for Implementation

---

## Executive Summary

This document provides a complete design for instrumenting RepoQL's indexing pipeline with OpenTelemetry metrics. The design addresses the current issue where 15+ defined metrics always report zero due to missing instrumentation and callback registration.

**Goals**:
- Provide complete observability into indexing pipeline performance
- Identify bottlenecks by stage and file type
- Track document lifecycle and graph extraction rates
- Monitor queue health and worker utilization
- Minimize complexity and performance overhead

**Approach**:
- **Static metrics class** - No DI wiring, simple usage
- **Instrument at stage boundaries** - Not within processing
- **Remove database totals** - Query on-demand via RepoQL instead
- **Remove ambiguous metrics** - Only keep what's clearly useful
- **Follow OpenTelemetry conventions** - Standard naming and semantics

**Key Decisions**:
1. ✅ **Static IndexingMetrics** - Eliminates DI complexity, no constructor changes
2. ✅ **No database total metrics** - Available via `repoql query`, adds unnecessary complexity
3. ✅ **37 metrics total** - 21 counters, 9 histograms, 7 observable gauges
4. ✅ **< 50 lines added** to hot path - Minimal complexity increase

---

## Current State Analysis

### Metrics That Work ✅

**HostMetrics** (RepoQL.ConsoleApp):
- `repoql.host.leases.active` - Active lease holders
- `repoql.host.writer.pending` - Pending write operations
- `repoql.host.implicit` - Host started implicitly
- `repoql.host.idle.seconds_until_shutdown` - Idle timer

**WorkQueue Metrics** (RepoQL.Core, RepoQL.Indexing):
- Queue depth, capacity, workers active (defined but not exposed to IndexingMetrics)

**DatabaseWriter Metrics** (RepoQL.Data.DuckDB):
- Queue wait time, write duration, batch timing (instrumented but could be improved)

### Metrics That Are Broken ❌

**IndexingMetrics** (RepoQL.Contracts):
- **Stage counters**: Never incremented (StageDiscover, StageHash, StageParse, StageIndex, StageEnrich)
- **Document lifecycle**: Never incremented (DocumentsCreated, DocumentsUpdated)
- **Graph extraction**: Never incremented (NodesExtracted, EntitiesCreated, OccurrencesCreated)
- **Durations**: Never recorded (ParseDuration, EnrichmentDuration, DbWriteDuration)
- **Observable gauges**: No callbacks registered (QueueDepth, WorkersActive, DbConnectionsActive, etc.)
- **Throughput**: Stateful with reset bugs (ThroughputFilesPerSecond, ThroughputBytesPerSecond)

### Root Cause

1. **No instance created**: `IndexingMetrics` class defined but never instantiated in pipeline
2. **No instrumentation**: Critical code paths don't increment counters or record histograms
3. **No callback registration**: Observable gauges have null callbacks, always return 0
4. **API mismatch**: Helper methods reference stages that don't exist in actual pipeline

---

## Design Principles

### 1. Static is Simpler
Use static metrics class. RepoQL runs one indexer per process, so instance metrics add unnecessary DI complexity.

### 2. Measure at Boundaries
Instrument at stage transitions, not within processing logic. This minimizes overhead and makes metrics easier to reason about.

### 3. Tag Richly
Include `mime_type`, `status`, `result` on all metrics to enable drill-down analysis. Accept 50+ mime_type values as necessary cardinality.

### 4. Avoid State Management
Don't cache values that can be queried on-demand. Let OpenTelemetry calculate rates from monotonic counters.

### 5. Name Consistently
- **Counters**: Past tense (`files.indexed`, `documents.created`)
- **Gauges**: Present tense (`queue.depth`, `workers.active`)
- **Histograms**: Neutral (`stage.duration`, `queue.wait_time`)

### 6. Follow OpenTelemetry Conventions
Use `repoql.` prefix, semantic units (`ms`, `bytes`, `files`), and standard tags.

---

## Metrics to Remove

### Ambiguous/Unused Metrics

| Metric | Reason for Removal |
|--------|-------------------|
| `FilesProcessed` | Ambiguous - does it mean enqueued, parsed, or indexed? Replaced by specific stage metrics |
| `StageDiscover` | Doesn't match actual pipeline stages (no "discover" stage exists) |
| `StageHash` | Doesn't match actual pipeline stages (no "hash" stage exists) |
| `StageIndex` | Confusing name - sounds like indexing, but pipeline has "commit" stage |
| `StageEnrich` | Doesn't match actual pipeline terminology (single-file vs multi-file analysis) |
| `EntitiesCreated` | Unclear semantics - are these nodes? edges? Never used in codebase |
| `OccurrencesCreated` | Unclear semantics - are these references? edges? Never used in codebase |
| `FileSystemEvents` | Different concern (file watcher), should be separate meter if needed |
| `ThroughputFilesPerSecond` | Stateful with reset logic - OTel can calculate from `files.indexed` counter |
| `ThroughputBytesPerSecond` | Stateful with reset logic - OTel can calculate from `bytes.indexed` counter |
| `TransactionsTotal` | Too generic - replaced by `transactions.committed` and `transactions.failed` |

### Database Total Metrics (Removed for Simplicity)

| Metric | Reason for Removal |
|--------|-------------------|
| `DbDocumentsTotal` | Available via `repoql query "SELECT COUNT(*) FROM node WHERE kind='document'"` |
| `DbNodesTotal` | Available via `repoql query "SELECT COUNT(*) FROM node"` |
| `DbEdgesTotal` | Available via `repoql query "SELECT COUNT(*) FROM edge"` |
| `DbAnnotationsTotal` | Available via `repoql query "SELECT COUNT(*) FROM annotation"` |
| `DbEmbeddingsTotal` | Available via `repoql query "SELECT COUNT(*) FROM document_embedding"` |
| `DbConnectionsActive` | DuckDB is single-connection, always 1 - not useful |

**Rationale**: These add significant complexity (80 lines of caching logic, threading concerns, state management) for slow-changing cumulative values that are easily queried on-demand. Writer queue performance metrics (depth, wait time, throughput) remain fully instrumented.

### Helper Methods to Remove

| Method | Reason for Removal |
|--------|-------------------|
| `RecordFileProcessed()` | Hides what's actually recorded, references `FilesProcessed` |
| `IncrementDiscover()` | References non-existent `StageDiscover` |
| `IncrementHash()` | References non-existent `StageHash` |
| `IncrementParse()` | Confusing - does it mean classified or parsed? Use explicit metrics |
| `IncrementIndex()` | References ambiguous `StageIndex` |
| `IncrementEnrich()` | References ambiguous `StageEnrich` |
| `CalculateFilesPerSecond()` | Stateful reset logic, unnecessary |
| `CalculateBytesPerSecond()` | Stateful reset logic, unnecessary |

### Observable Gauge Callbacks to Remove

| Callback Field | Reason for Removal |
|---------------|-------------------|
| Individual callback fields | Replaced by multi-measurement pattern (single callback returns all queue measurements) |
| `_memoryUsageCallback` | Falls back to `GC.GetTotalMemory`, but not particularly useful - remove |
| `_dbConnectionsActiveCallback` | DuckDB is single-connection, always 1 - not useful |
| Database total callbacks | Removed with database total metrics |

### Fields to Remove

| Field | Reason for Removal |
|-------|-------------------|
| `_lastFilesProcessed` | Used by removed throughput metrics |
| `_lastBytesProcessed` | Used by removed throughput metrics |
| `_lastMeasurement` | Used by removed throughput metrics |
| `_rateLock` | Used by removed throughput metrics |

---

## Proposed Metrics (Complete List)

**Total**: 37 metrics (21 counters, 9 histograms, 7 observable gauges)

### Counters (21 metrics)

| Metric Name | Unit | Description | Tags |
|-------------|------|-------------|------|
| `repoql.files.enqueued` | files | Files added to indexing queue | mime_type, read_only |
| `repoql.files.filtered` | files | Files rejected by filters (.gitignore, size) | reason, mime_type |
| `repoql.files.skipped` | files | Files skipped (up-to-date, no changes) | reason, mime_type |
| `repoql.files.classified` | files | Files successfully classified | mime_type, result |
| `repoql.files.parsed` | files | Files successfully parsed | mime_type, result |
| `repoql.files.enriched` | files | Files analyzed (single-file) | mime_type, result |
| `repoql.files.indexed` | files | Files committed to database | mime_type, status |
| `repoql.files.errored` | files | Files that failed processing | mime_type, error_type, stage |
| `repoql.files.pruned` | files | Files identified as deleted during prune | - |
| `repoql.documents.created` | documents | New documents inserted | mime_type |
| `repoql.documents.updated` | documents | Existing documents updated | mime_type |
| `repoql.documents.deleted` | documents | Documents removed from index | - |
| `repoql.nodes.extracted` | nodes | Graph nodes extracted from documents | mime_type |
| `repoql.edges.extracted` | edges | Graph edges extracted from documents | mime_type |
| `repoql.spans.extracted` | spans | Source location spans extracted | mime_type |
| `repoql.annotations.upserted` | annotations | Annotations added/updated | operation |
| `repoql.epochs.completed` | epochs | Indexing epochs completed | - |
| `repoql.idle.cycles` | cycles | Idle processing cycles triggered | - |
| `repoql.transactions.committed` | transactions | Database transactions committed | operation_type, mode |
| `repoql.transactions.failed` | transactions | Database transactions failed | operation_type, error_type |
| `repoql.batches.committed` | batches | Writer batches committed | batch_size_bucket |

### Histograms (9 metrics)

| Metric Name | Unit | Description | Tags | Buckets |
|-------------|------|-------------|------|---------|
| `repoql.stage.duration` | ms | Time spent in each pipeline stage | stage, mime_type | Default OTel |
| `repoql.hot_path.duration` | ms | Total time for file through hot path | mime_type, status | Default OTel |
| `repoql.db.write.duration` | ms | Database write operation duration | operation_type, mode | Default OTel |
| `repoql.batch.duration` | ms | Batch transaction duration | batch_size | Default OTel |
| `repoql.queue.wait_time` | ms | Time spent waiting in writer queue | - | [0, 1, 5, 10, 25, 50, 100, 250, 500, 1000] |
| `repoql.epoch.size` | items | Total items processed in epoch | - | [0, 10, 50, 100, 250, 500, 1000, 5000, 10000] |
| `repoql.epoch.duration` | ms | Total epoch idle processing duration | - | Default OTel |
| `repoql.idle.phase.duration` | ms | Duration of idle processing phase | phase | Default OTel |
| `repoql.file.size` | bytes | Size of processed files | mime_type | [0, 1024, 10KB, 100KB, 1MB, 10MB, 100MB] |

**Note**: "Default OTel" buckets = [0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000]

### Observable Gauges (7 metrics)

| Metric Name | Unit | Description | Tags | Source |
|-------------|------|-------------|------|--------|
| `repoql.queue.depth` | items | Current items in queue | queue | IndexerQueue, AnalysisQueue, Writer |
| `repoql.queue.capacity` | items | Maximum queue capacity | queue | Constant per queue |
| `repoql.workers.active` | workers | Workers currently processing | queue | WorkQueue._busy |
| `repoql.catalog.entries` | entries | Total entries in document catalog | - | DocumentCatalog._entries.Count |
| `repoql.catalog.pending` | entries | Pending digest computations | - | DocumentCatalog._pendingDigests.Count |
| `repoql.epoch.current` | - | Current epoch number | - | EpochTracker.CurrentEpoch |
| `repoql.epoch.pending_items` | items | Pending items in current epoch | - | EpochTracker.CurrentPendingItems |

**Note**: Database totals (documents, nodes, edges, annotations, embeddings) are **NOT** included. Query on-demand via:
```bash
repoql query "SELECT COUNT(*) FROM node WHERE kind='document'"
repoql query "SELECT * FROM Files"
```

---

## Tag Definitions

### Standard Tags (All Metrics)

| Tag | Values | Cardinality | Purpose |
|-----|--------|-------------|---------|
| `mime_type` | "text/markdown.doc", "text/x-csharp", etc. | ~50 | Identify per-format performance |
| `status` | "success", "error", "filtered", "skipped" | 4 | Track success rate |
| `result` | "Success", "Filtered", "Error" | 3 | Pipeline result (from PipelineResult enum) |

### Stage-Specific Tags

| Tag | Values | Used By | Purpose |
|-----|--------|---------|---------|
| `stage` | "classification", "parsing", "single_file_analysis", "commit", "multi_file_analysis" | stage.duration, files.errored | Identify bottleneck stage |
| `queue` | "indexer", "analysis", "writer" | queue.depth, workers.active | Distinguish queue types |
| `operation_type` | "ReplaceDocument", "UpsertAnnotations", "DeleteDocument", "Barrier" | transactions.*, db.write.duration | Track operation distribution |
| `mode` | "single", "batch" | transactions.committed, db.write.duration | Batch vs single-item path |
| `phase` | "prune", "vector_refresh", "multi_file_analysis" | idle.phase.duration | Idle processing breakdown |
| `reason` | "up_to_date", "gitignore", "size_limit" | files.filtered, files.skipped | Why files excluded |
| `error_type` | Exception type name (top 20 + "other") | files.errored, transactions.failed | Error distribution |
| `batch_size` | Actual batch size (for histograms) | batch.duration | Correlate batch size with duration |
| `batch_size_bucket` | "1-10", "11-32", "33-64" | batches.committed | Batch size distribution |
| `read_only` | "true", "false" | files.enqueued | Track read-only vs writable mounts |
| `operation` | "upsert", "delete" | annotations.upserted | Annotation operation type |

### Tag Cardinality Limits

**High cardinality tags** (require special handling):
- `error_type`: Unbounded (exception types) → Limit to top 20 + "other"
- `epoch`: Unbounded monotonic → Don't use as tag, include in exemplars only

**Acceptable cardinality**:
- `mime_type`: ~50 values (necessary for analysis)
- All other tags: < 10 values

---

## Architecture

### Static Metrics Pattern

```csharp
// IndexingMetrics.cs (static)
public static class IndexingMetrics
{
    private static readonly Meter _meter = new("RepoQL.Indexing", "1.0.0");

    // Counters (trivially static)
    public static readonly Counter<long> FilesIndexed =
        _meter.CreateCounter<long>("repoql.files.indexed", "files");

    // Histograms (trivially static)
    public static readonly Histogram<double> StageDuration =
        _meter.CreateHistogram<double>("repoql.stage.duration", "ms");

    // Observable gauges (need callback registration)
    private static Func<IEnumerable<Measurement<int>>>? _queueDepthCallback;

    public static readonly ObservableGauge<int> QueueDepth =
        _meter.CreateObservableGauge("repoql.queue.depth",
            () => _queueDepthCallback?.Invoke() ?? Enumerable.Empty<Measurement<int>>(),
            "items");

    // Registration (called once at startup)
    public static void RegisterQueueCallbacks(
        Func<int> indexerDepth,
        Func<int> analysisDepth,
        Func<int> writerDepth)
    {
        _queueDepthCallback = () => new[]
        {
            new Measurement<int>(indexerDepth(), new TagList { { "queue", "indexer" } }),
            new Measurement<int>(analysisDepth(), new TagList { { "queue", "analysis" } }),
            new Measurement<int>(writerDepth(), new TagList { { "queue", "writer" } })
        };
    }
}

// Usage in IndexingEngine (no DI)
public IndexingEngine(
    WorkQueue<IndexItem> indexerQueue,
    WorkQueue<IndexItem> analysisQueue,
    DatabaseWriter writer,
    // ... other dependencies
)
{
    // Register observable gauge callbacks once
    IndexingMetrics.RegisterQueueCallbacks(
        () => indexerQueue.Depth,
        () => analysisQueue.Depth,
        () => writer.GetStatus().PendingCount);

    // ... rest of constructor
}

// Usage anywhere (no instance needed)
IndexingMetrics.FilesIndexed.Add(1, new TagList
{
    { "mime_type", item.MediaType?.ToString() ?? "unknown" },
    { "status", "success" }
});
```

**Benefits**:
- ✅ No DI registration needed
- ✅ No constructor parameter on IndexingEngine
- ✅ No constructor parameter on DatabaseWriter
- ✅ Simple usage: `IndexingMetrics.FilesIndexed.Add(1, tags)`
- ✅ Observable gauges registered once at startup

**Trade-offs**:
- ❌ Shared state across tests (acceptable - we test indexing, not metrics)
- ❌ Manual disposal (acceptable - Meter disposal not critical)
- ❌ Can't have multiple independent indexers (not a RepoQL requirement)

### Component Ownership

```
IndexingEngine
  ├─ Calls IndexingMetrics.RegisterXxxCallbacks() in constructor
  ├─ Increments stage counters (classified, parsed, enriched, indexed)
  ├─ Records stage durations (classification, parsing, enrichment, commit)
  └─ Increments idle processing counters (epochs, cycles, pruned)

DatabaseWriter (SingleThreadedDatabaseWriter)
  ├─ Increments document lifecycle (created, updated, deleted)
  ├─ Increments graph extraction (nodes, edges, spans)
  ├─ Increments transaction counters (committed, failed)
  └─ Records write durations (db.write.duration, batch.duration)

DocumentCatalog
  └─ Exposes state via properties (no metrics ownership)
      ├─ EntryCount
      └─ PendingDigestCount

EpochTracker
  └─ Exposes state via properties (no metrics ownership)
      ├─ CurrentEpoch
      └─ CurrentPendingItems

WorkQueue<T>
  └─ Exposes state via properties (no metrics ownership)
      ├─ Depth
      └─ (Workers active via _busy field)
```

**Key insight**: Static metrics class, but components expose state via instance properties for observable gauge callbacks.

---

## Instrumentation Locations

### IndexingEngine.cs

#### Constructor Changes

```csharp
public IndexingEngine(
    WorkQueue<IndexItem> indexerQueue,
    WorkQueue<IndexItem> analysisQueue,
    IDatabaseWriter writer,
    DocumentCatalog catalog,
    EpochTracker epochTracker,
    // ... other dependencies
)
{
    _indexerQueue = indexerQueue;
    _analysisQueue = analysisQueue;
    // ... other initialization

    // Register observable gauge callbacks (multi-measurement pattern)
    IndexingMetrics.RegisterQueueCallbacks(
        indexerDepth: () => indexerQueue.Depth,
        analysisDepth: () => analysisQueue.Depth,
        writerDepth: () => writer.GetStatus().PendingCount,
        indexerCapacity: () => indexerQueue.MaxDepth,
        analysisCapacity: () => analysisQueue.MaxDepth,
        writerCapacity: () => 1000, // SingleThreadedDatabaseWriter constant
        indexerWorkers: () => indexerQueue.ActiveWorkers,
        analysisWorkers: () => analysisQueue.ActiveWorkers);

    IndexingMetrics.RegisterCatalogCallbacks(
        entryCount: () => catalog.EntryCount,
        pendingCount: () => catalog.PendingDigestCount);

    IndexingMetrics.RegisterEpochCallbacks(
        currentEpoch: () => epochTracker.CurrentEpoch,
        pendingItems: () => epochTracker.CurrentPendingItems);
}
```

#### IndexItemAsync Method

**Location: Line 340-350 (after filter check)**
```csharp
IndexingMetrics.FilesEnqueued.Add(1, new TagList
{
    { "mime_type", item.MediaType?.ToString() ?? "unknown" },
    { "read_only", item.ReadOnly.ToString().ToLowerInvariant() }
});

// If filtered out
if (filtered)
{
    IndexingMetrics.FilesFiltered.Add(1, new TagList
    {
        { "reason", filterReason }, // "gitignore", "size_limit", etc.
        { "mime_type", item.MediaType?.ToString() ?? "unknown" }
    });
    return;
}
```

**Location: Line 370-380 (after catalog check)**
```csharp
var evaluation = DocumentCatalog.Evaluate(item.Uri, digestHex);
if (item.Options.HasFlag(IndexItemOptions.OnlyIfStale) &&
    evaluation.Decision == DocumentCatalogDecision.SkipUpToDate)
{
    IndexingMetrics.FilesSkipped.Add(1, new TagList
    {
        { "reason", "up_to_date" },
        { "mime_type", item.MediaType?.ToString() ?? "unknown" }
    });
    return;
}
```

**Location: Line 397 (after commit success)**
```csharp
var commitTimer = Stopwatch.StartNew();
await Committer.CommitAsync(item, ct);
commitTimer.Stop();

IndexingMetrics.FilesIndexed.Add(1, new TagList
{
    { "mime_type", item.MediaType?.ToString() ?? "unknown" },
    { "status", "success" }
});

IndexingMetrics.StageDuration.Record(commitTimer.Elapsed.TotalMilliseconds, new TagList
{
    { "stage", "commit" },
    { "mime_type", item.MediaType?.ToString() ?? "unknown" }
});
```

**Location: Line 410-420 (error handling)**
```csharp
catch (Exception ex)
{
    IndexingMetrics.FilesErrored.Add(1, new TagList
    {
        { "mime_type", item.MediaType?.ToString() ?? "unknown" },
        { "error_type", TruncateErrorType(ex.GetType().Name) },
        { "stage", currentStage } // Track which stage failed
    });
    throw;
}
```

**Location: Line 425 (finally block - hot path duration)**
```csharp
finally
{
    var totalDuration = hotPathTimer.Elapsed.TotalMilliseconds;
    IndexingMetrics.HotPathDuration.Record(totalDuration, new TagList
    {
        { "mime_type", item.MediaType?.ToString() ?? "unknown" },
        { "status", success ? "success" : "error" }
    });

    var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
    if (epochBecameIdle && State == IndexingState.AllIdle)
        HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
}
```

#### ApplyIndexerPipeline Method

**Location: Line 666 (classification stage)**
```csharp
var classifyTimer = Stopwatch.StartNew();
var classifyResult = await _classificationStage.RunAsync(item, ct);
classifyTimer.Stop();

IndexingMetrics.FilesClassified.Add(1, new TagList
{
    { "mime_type", item.MediaType?.ToString() ?? "unknown" },
    { "result", classifyResult.ToString() }
});

IndexingMetrics.StageDuration.Record(classifyTimer.Elapsed.TotalMilliseconds, new TagList
{
    { "stage", "classification" },
    { "mime_type", item.MediaType?.ToString() ?? "unknown" }
});

if (classifyResult != PipelineResult.Success)
    return classifyResult;
```

**Location: Line 674 (parsing stage)**
```csharp
var parseTimer = Stopwatch.StartNew();
var parseResult = await _parsingStage.RunAsync(item, ct);
parseTimer.Stop();

IndexingMetrics.FilesParsed.Add(1, new TagList
{
    { "mime_type", item.MediaType?.ToString() ?? "unknown" },
    { "result", parseResult.ToString() }
});

IndexingMetrics.StageDuration.Record(parseTimer.Elapsed.TotalMilliseconds, new TagList
{
    { "stage", "parsing" },
    { "mime_type", item.MediaType?.ToString() ?? "unknown" }
});

if (parseResult != PipelineResult.Success)
    return parseResult;
```

**Location: Line 688 (single-file analysis stage)**
```csharp
var enrichTimer = Stopwatch.StartNew();
var enrichResult = await _singleFileStage.RunAsync(item, ct);
enrichTimer.Stop();

IndexingMetrics.FilesEnriched.Add(1, new TagList
{
    { "mime_type", item.MediaType?.ToString() ?? "unknown" },
    { "result", enrichResult.ToString() }
});

IndexingMetrics.StageDuration.Record(enrichTimer.Elapsed.TotalMilliseconds, new TagList
{
    { "stage", "single_file_analysis" },
    { "mime_type", item.MediaType?.ToString() ?? "unknown" }
});

return enrichResult;
```

#### Idle Processing Methods

**Location: OnHotPathIdle (line 477)**
```csharp
private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args)
{
    IndexingMetrics.IdleCycles.Add(1);

    // ... existing idle trigger logic
}
```

**Location: ReleaseAnalysisAsync (line 589)**
```csharp
private async Task ReleaseAnalysisAsync(int epoch, CancellationToken ct)
{
    // Prune phase
    var pruneTimer = Stopwatch.StartNew();
    var prunedFiles = await PruneDeletedFilesAsync(epoch, ct);
    pruneTimer.Stop();

    IndexingMetrics.FilesPruned.Add(prunedFiles.Count);
    IndexingMetrics.IdlePhaseDuration.Record(pruneTimer.Elapsed.TotalMilliseconds,
        new TagList { { "phase", "prune" } });

    // Vector refresh phase
    var vectorTimer = Stopwatch.StartNew();
    await RefreshVectorEmbeddingsAsync(ct);
    vectorTimer.Stop();

    IndexingMetrics.IdlePhaseDuration.Record(vectorTimer.Elapsed.TotalMilliseconds,
        new TagList { { "phase", "vector_refresh" } });

    // Multi-file analysis phase
    var analysisTimer = Stopwatch.StartNew();
    await EnqueueMultiFileAnalysisAsync(epoch, ct);
    analysisTimer.Stop();

    IndexingMetrics.IdlePhaseDuration.Record(analysisTimer.Elapsed.TotalMilliseconds,
        new TagList { { "phase", "multi_file_analysis" } });
}
```

**Location: CompleteEpochActivity (line 527)**
```csharp
private void CompleteEpochActivity(int epoch, Stopwatch epochTimer)
{
    IndexingMetrics.EpochsCompleted.Add(1);

    var epochSize = _epochTracker.GetEpochTotalItems(epoch);
    IndexingMetrics.EpochSize.Record(epochSize);

    IndexingMetrics.EpochDuration.Record(epochTimer.Elapsed.TotalMilliseconds);
}
```

### SingleThreadedDatabaseWriter.cs

**No constructor changes needed** - Static metrics, no DI.

#### ApplyReplaceDocumentAsync Method

**Location: Line 401 (document create/update detection)**
```csharp
private async Task<WriteResult> ApplyReplaceDocumentAsync(
    WriteOperation op,
    DuckDBCommand cmd,
    CancellationToken ct)
{
    var records = op.Records!;
    var mimeType = records.Artifact.MediaType;

    // Check if document exists
    cmd.CommandText = "SELECT 1 FROM node WHERE uri = $1 AND kind = 'document'";
    cmd.Parameters.Clear();
    cmd.Parameters.Add(new DuckDBParameter(op.Uri));
    var exists = await cmd.ExecuteScalarAsync(ct) != null;

    // ... execute UPSERT operations ...

    // Record metrics after successful commit
    if (exists)
    {
        IndexingMetrics.DocumentsUpdated.Add(1, new TagList { { "mime_type", mimeType } });
    }
    else
    {
        IndexingMetrics.DocumentsCreated.Add(1, new TagList { { "mime_type", mimeType } });
    }

    // Record graph extraction
    var nodeCount = 1 + records.ChildNodes.Count; // doc node + children
    IndexingMetrics.NodesExtracted.Add(nodeCount, new TagList { { "mime_type", mimeType } });
    IndexingMetrics.EdgesExtracted.Add(records.Edges.Count, new TagList { { "mime_type", mimeType } });
    IndexingMetrics.SpansExtracted.Add(records.Spans.Count, new TagList { { "mime_type", mimeType } });

    return WriteResult.Success;
}
```

#### ApplyDeleteDocument Method

**Location: Line 526**
```csharp
private async Task<WriteResult> ApplyDeleteDocument(
    WriteOperation op,
    DuckDBCommand cmd,
    CancellationToken ct)
{
    // ... execute DELETE ...

    IndexingMetrics.DocumentsDeleted.Add(1);

    return WriteResult.Success;
}
```

#### ApplyUpsertAnnotations Method

**Location: Line 519**
```csharp
private async Task<WriteResult> ApplyUpsertAnnotations(
    WriteOperation op,
    DuckDBCommand cmd,
    CancellationToken ct)
{
    var annotations = op.Annotations!;

    // ... execute UPSERT ...

    IndexingMetrics.AnnotationsUpserted.Add(annotations.Count,
        new TagList { { "operation", "upsert" } });

    return WriteResult.Success;
}
```

#### ProcessOneAsync Method

**Location: Line 206-214 (success path)**
```csharp
private async Task ProcessOneAsync(WriteOperation op, CancellationToken ct)
{
    var timer = Stopwatch.StartNew();
    try
    {
        await using var cmd = _connection.CreateCommand();
        await using var tx = _connection.BeginTransaction();

        var result = op.Type switch
        {
            WriteOperationType.ReplaceDocument => await ApplyReplaceDocumentAsync(op, cmd, ct),
            WriteOperationType.DeleteDocument => await ApplyDeleteDocument(op, cmd, ct),
            WriteOperationType.UpsertAnnotations => await ApplyUpsertAnnotations(op, cmd, ct),
            WriteOperationType.Barrier => WriteResult.Success,
            _ => throw new InvalidOperationException($"Unknown operation type: {op.Type}")
        };

        await tx.CommitAsync(ct);
        timer.Stop();

        IndexingMetrics.TransactionsCommitted.Add(1, new TagList
        {
            { "operation_type", op.Type.ToString() },
            { "mode", "single" }
        });

        IndexingMetrics.DbWriteDuration.Record(timer.Elapsed.TotalMilliseconds, new TagList
        {
            { "operation_type", op.Type.ToString() },
            { "mode", "single" }
        });

        op.OnCommitted?.Invoke(op, result);
    }
    catch (Exception ex)
    {
        timer.Stop();

        IndexingMetrics.TransactionsFailed.Add(1, new TagList
        {
            { "operation_type", op.Type.ToString() },
            { "error_type", TruncateErrorType(ex.GetType().Name) }
        });

        throw;
    }
}
```

#### TryProcessBatchAsync Method

**Location: Line 344-355 (batch path)**
```csharp
private async Task<bool> TryProcessBatchAsync(List<WriteOperation> batch, CancellationToken ct)
{
    var timer = Stopwatch.StartNew();
    try
    {
        await using var tx = _connection.BeginTransaction();

        foreach (var op in batch)
        {
            // ... execute operations ...
        }

        await tx.CommitAsync(ct);
        timer.Stop();

        var batchSizeBucket = batch.Count switch
        {
            <= 10 => "1-10",
            <= 32 => "11-32",
            _ => "33-64"
        };

        IndexingMetrics.BatchesCommitted.Add(1, new TagList
        {
            { "batch_size_bucket", batchSizeBucket }
        });

        IndexingMetrics.BatchDuration.Record(timer.Elapsed.TotalMilliseconds, new TagList
        {
            { "batch_size", batch.Count.ToString() }
        });

        IndexingMetrics.TransactionsCommitted.Add(1, new TagList
        {
            { "operation_type", "Batch" },
            { "mode", "batch" }
        });

        // Fire callbacks
        foreach (var op in batch)
            op.OnCommitted?.Invoke(op, WriteResult.Success);

        return true;
    }
    catch
    {
        timer.Stop();
        return false; // Fall back to single-item processing
    }
}
```

### DocumentCatalog.cs

```csharp
// Add properties
public int EntryCount => _entries.Count;
public int PendingDigestCount => _pendingDigests.Count;
```

### EpochTracker.cs

```csharp
// Add fields
private readonly ConcurrentDictionary<int, int> _peakByEpoch = new();

// Add properties
public int CurrentEpoch => _currentEpoch;
public int CurrentPendingItems =>
    _pendingByEpoch.TryGetValue(_currentEpoch, out var count) ? count : 0;

// Update Increment to track peak
public void Increment(int epoch)
{
    var newCount = _pendingByEpoch.AddOrUpdate(epoch, 1, (_, old) => old + 1);
    _peakByEpoch.AddOrUpdate(epoch, newCount, (_, old) => Math.Max(old, newCount));
}

// Add method to get total items (for histogram)
public int GetEpochTotalItems(int epoch)
{
    return _peakByEpoch.TryGetValue(epoch, out var peak) ? peak : 0;
}
```

### WorkQueue.cs (Expose ActiveWorkers)

```csharp
// Add property to expose _busy field
public int ActiveWorkers => _busy;
```

---

## IndexingMetrics Class Implementation

### Complete Static Class Structure

```csharp
using System.Diagnostics.Metrics;

namespace RepoQL.Metrics;

/// <summary>
/// Static metrics for RepoQL indexing engine using OpenTelemetry.
/// </summary>
public static class IndexingMetrics
{
    private static readonly Meter _meter = new("RepoQL.Indexing", "1.0.0");

    // ============================================================================
    // COUNTERS
    // ============================================================================

    public static readonly Counter<long> FilesEnqueued = _meter.CreateCounter<long>(
        "repoql.files.enqueued", "files", "Files added to indexing queue");

    public static readonly Counter<long> FilesFiltered = _meter.CreateCounter<long>(
        "repoql.files.filtered", "files", "Files rejected by filters");

    public static readonly Counter<long> FilesSkipped = _meter.CreateCounter<long>(
        "repoql.files.skipped", "files", "Files skipped (up-to-date)");

    public static readonly Counter<long> FilesClassified = _meter.CreateCounter<long>(
        "repoql.files.classified", "files", "Files successfully classified");

    public static readonly Counter<long> FilesParsed = _meter.CreateCounter<long>(
        "repoql.files.parsed", "files", "Files successfully parsed");

    public static readonly Counter<long> FilesEnriched = _meter.CreateCounter<long>(
        "repoql.files.enriched", "files", "Files analyzed (single-file)");

    public static readonly Counter<long> FilesIndexed = _meter.CreateCounter<long>(
        "repoql.files.indexed", "files", "Files committed to database");

    public static readonly Counter<long> FilesErrored = _meter.CreateCounter<long>(
        "repoql.files.errored", "files", "Files that failed processing");

    public static readonly Counter<long> FilesPruned = _meter.CreateCounter<long>(
        "repoql.files.pruned", "files", "Files identified as deleted during prune");

    public static readonly Counter<long> DocumentsCreated = _meter.CreateCounter<long>(
        "repoql.documents.created", "documents", "New documents inserted");

    public static readonly Counter<long> DocumentsUpdated = _meter.CreateCounter<long>(
        "repoql.documents.updated", "documents", "Existing documents updated");

    public static readonly Counter<long> DocumentsDeleted = _meter.CreateCounter<long>(
        "repoql.documents.deleted", "documents", "Documents removed from index");

    public static readonly Counter<long> NodesExtracted = _meter.CreateCounter<long>(
        "repoql.nodes.extracted", "nodes", "Graph nodes extracted from documents");

    public static readonly Counter<long> EdgesExtracted = _meter.CreateCounter<long>(
        "repoql.edges.extracted", "edges", "Graph edges extracted from documents");

    public static readonly Counter<long> SpansExtracted = _meter.CreateCounter<long>(
        "repoql.spans.extracted", "spans", "Source location spans extracted");

    public static readonly Counter<long> AnnotationsUpserted = _meter.CreateCounter<long>(
        "repoql.annotations.upserted", "annotations", "Annotations added/updated");

    public static readonly Counter<long> EpochsCompleted = _meter.CreateCounter<long>(
        "repoql.epochs.completed", "epochs", "Indexing epochs completed");

    public static readonly Counter<long> IdleCycles = _meter.CreateCounter<long>(
        "repoql.idle.cycles", "cycles", "Idle processing cycles triggered");

    public static readonly Counter<long> TransactionsCommitted = _meter.CreateCounter<long>(
        "repoql.transactions.committed", "transactions", "Database transactions committed");

    public static readonly Counter<long> TransactionsFailed = _meter.CreateCounter<long>(
        "repoql.transactions.failed", "transactions", "Database transactions failed");

    public static readonly Counter<long> BatchesCommitted = _meter.CreateCounter<long>(
        "repoql.batches.committed", "batches", "Writer batches committed");

    // ============================================================================
    // HISTOGRAMS
    // ============================================================================

    public static readonly Histogram<double> StageDuration = _meter.CreateHistogram<double>(
        "repoql.stage.duration", "ms", "Time spent in each pipeline stage");

    public static readonly Histogram<double> HotPathDuration = _meter.CreateHistogram<double>(
        "repoql.hot_path.duration", "ms", "Total time for file through hot path");

    public static readonly Histogram<double> DbWriteDuration = _meter.CreateHistogram<double>(
        "repoql.db.write.duration", "ms", "Database write operation duration");

    public static readonly Histogram<double> BatchDuration = _meter.CreateHistogram<double>(
        "repoql.batch.duration", "ms", "Batch transaction duration");

    public static readonly Histogram<double> QueueWaitTime = _meter.CreateHistogram<double>(
        "repoql.queue.wait_time", "ms", "Time spent waiting in writer queue");

    public static readonly Histogram<double> EpochSize = _meter.CreateHistogram<double>(
        "repoql.epoch.size", "items", "Total items processed in epoch");

    public static readonly Histogram<double> EpochDuration = _meter.CreateHistogram<double>(
        "repoql.epoch.duration", "ms", "Total epoch idle processing duration");

    public static readonly Histogram<double> IdlePhaseDuration = _meter.CreateHistogram<double>(
        "repoql.idle.phase.duration", "ms", "Duration of idle processing phase");

    public static readonly Histogram<long> FileSize = _meter.CreateHistogram<long>(
        "repoql.file.size", "bytes", "Size of processed files");

    // ============================================================================
    // OBSERVABLE GAUGES (with callbacks)
    // ============================================================================

    private static Func<IEnumerable<Measurement<int>>>? _queueDepthCallback;
    private static Func<IEnumerable<Measurement<int>>>? _queueCapacityCallback;
    private static Func<IEnumerable<Measurement<int>>>? _workersActiveCallback;
    private static Func<int>? _catalogEntriesCallback;
    private static Func<int>? _catalogPendingCallback;
    private static Func<int>? _epochCurrentCallback;
    private static Func<int>? _epochPendingCallback;

    public static readonly ObservableGauge<int> QueueDepth = _meter.CreateObservableGauge(
        "repoql.queue.depth",
        () => _queueDepthCallback?.Invoke() ?? Enumerable.Empty<Measurement<int>>(),
        "items",
        "Current items in queue");

    public static readonly ObservableGauge<int> QueueCapacity = _meter.CreateObservableGauge(
        "repoql.queue.capacity",
        () => _queueCapacityCallback?.Invoke() ?? Enumerable.Empty<Measurement<int>>(),
        "items",
        "Maximum queue capacity");

    public static readonly ObservableGauge<int> WorkersActive = _meter.CreateObservableGauge(
        "repoql.workers.active",
        () => _workersActiveCallback?.Invoke() ?? Enumerable.Empty<Measurement<int>>(),
        "workers",
        "Workers currently processing items");

    public static readonly ObservableGauge<int> CatalogEntries = _meter.CreateObservableGauge(
        "repoql.catalog.entries",
        () => _catalogEntriesCallback?.Invoke() ?? 0,
        "entries",
        "Total entries in document catalog");

    public static readonly ObservableGauge<int> CatalogPending = _meter.CreateObservableGauge(
        "repoql.catalog.pending",
        () => _catalogPendingCallback?.Invoke() ?? 0,
        "entries",
        "Pending digest computations");

    public static readonly ObservableGauge<int> EpochCurrent = _meter.CreateObservableGauge(
        "repoql.epoch.current",
        () => _epochCurrentCallback?.Invoke() ?? 0,
        description: "Current epoch number");

    public static readonly ObservableGauge<int> EpochPendingItems = _meter.CreateObservableGauge(
        "repoql.epoch.pending_items",
        () => _epochPendingCallback?.Invoke() ?? 0,
        "items",
        "Pending items in current epoch");

    // ============================================================================
    // CALLBACK REGISTRATION
    // ============================================================================

    public static void RegisterQueueCallbacks(
        Func<int> indexerDepth,
        Func<int> analysisDepth,
        Func<int> writerDepth,
        Func<int> indexerCapacity,
        Func<int> analysisCapacity,
        Func<int> writerCapacity,
        Func<int> indexerWorkers,
        Func<int> analysisWorkers)
    {
        _queueDepthCallback = () => new[]
        {
            new Measurement<int>(indexerDepth(), new TagList { { "queue", "indexer" } }),
            new Measurement<int>(analysisDepth(), new TagList { { "queue", "analysis" } }),
            new Measurement<int>(writerDepth(), new TagList { { "queue", "writer" } })
        };

        _queueCapacityCallback = () => new[]
        {
            new Measurement<int>(indexerCapacity(), new TagList { { "queue", "indexer" } }),
            new Measurement<int>(analysisCapacity(), new TagList { { "queue", "analysis" } }),
            new Measurement<int>(writerCapacity(), new TagList { { "queue", "writer" } })
        };

        _workersActiveCallback = () => new[]
        {
            new Measurement<int>(indexerWorkers(), new TagList { { "queue", "indexer" } }),
            new Measurement<int>(analysisWorkers(), new TagList { { "queue", "analysis" } })
        };
    }

    public static void RegisterCatalogCallbacks(
        Func<int> entryCount,
        Func<int> pendingCount)
    {
        _catalogEntriesCallback = entryCount;
        _catalogPendingCallback = pendingCount;
    }

    public static void RegisterEpochCallbacks(
        Func<int> currentEpoch,
        Func<int> pendingItems)
    {
        _epochCurrentCallback = currentEpoch;
        _epochPendingCallback = pendingItems;
    }

    // ============================================================================
    // HELPERS
    // ============================================================================

    /// <summary>
    /// Truncate error type to limit cardinality (top 20 + "other")
    /// </summary>
    public static string TruncateErrorType(string errorType)
    {
        var knownErrors = new HashSet<string>
        {
            "IOException", "UnauthorizedAccessException", "FileNotFoundException",
            "DuckDBException", "ArgumentException", "InvalidOperationException",
            "TimeoutException", "OperationCanceledException", "SqlException",
            "NullReferenceException", "NotSupportedException", "FormatException",
            "ArgumentNullException", "ArgumentOutOfRangeException", "IndexOutOfRangeException",
            "KeyNotFoundException", "OutOfMemoryException", "StackOverflowException",
            "AccessViolationException", "ObjectDisposedException"
        };

        return knownErrors.Contains(errorType) ? errorType : "other";
    }
}
```

---

## Performance Impact Analysis

### Hot Path Overhead Per File

| Operation | Count | Time per Op | Total |
|-----------|-------|-------------|-------|
| Counter.Add() | 7 calls | 20 ns | 140 ns |
| Histogram.Record() | 5 calls | 100 ns | 500 ns |
| TagList allocation | 12 allocs | 50 ns | 600 ns |
| **Total** | - | - | **1.24 μs** |

**Context**: A 1KB file takes 5-50ms to process. **Overhead: 0.002% - 0.02%**

### Memory Overhead

| Component | Size |
|-----------|------|
| Counter (21 metrics) | 21 × 16 bytes = 336 bytes |
| Histogram (9 metrics) | 9 × 200 bytes = 1.8 KB |
| Observable gauge (7 metrics) | 7 × 8 bytes = 56 bytes |
| Callback fields | 7 × 8 bytes = 56 bytes |
| **Total** | **~2.3 KB** |

### Lines of Code Added

| Component | Lines Added |
|-----------|-------------|
| IndexingEngine.cs | ~50 lines |
| SingleThreadedDatabaseWriter.cs | ~40 lines |
| DocumentCatalog.cs | 2 lines |
| EpochTracker.cs | 15 lines |
| WorkQueue.cs | 1 line |
| **Total** | **~108 lines** |

**Compare to database caching approach**: Would have been 200+ lines with threading complexity.

---

## Testing Strategy

### Unit Tests

**Test: Counters increment at correct locations**
```csharp
[Fact]
public async Task IndexItemAsync_IncrementsFilesIndexed_WhenCommitSucceeds()
{
    // Arrange
    var engine = CreateEngine();
    var item = CreateTestItem("text/markdown.doc");

    // Act
    await engine.IndexItemAsync(item, CancellationToken.None);

    // Assert - metrics accumulate across tests, just verify incrementing
    // (Can't test exact values with static metrics)
}
```

**Test: Observable gauges read correct values**
```csharp
[Fact]
public void QueueDepth_ReadsFromIndexerQueue()
{
    // Arrange
    var queue = new WorkQueue<IndexItem>(capacity: 100, workers: 4, processItem: async _ => {});
    IndexingMetrics.RegisterQueueCallbacks(
        indexerDepth: () => queue.Depth,
        // ... other callbacks
    );

    // Act
    queue.EnqueueAsync(CreateTestItem(), CancellationToken.None).Wait();

    // Assert
    // Observable gauges polled by OpenTelemetry MeterProvider
    // Manual verification via metric reader or Aspire dashboard
}
```

### Integration Tests

**Test: Metrics appear in OTLP export**
```csharp
[Fact]
public async Task MetricsExporter_ExportsAllMetrics()
{
    // Arrange
    var exportedMetrics = new List<Metric>();
    var meterProvider = Sdk.CreateMeterProviderBuilder()
        .AddMeter("RepoQL.Indexing")
        .AddInMemoryExporter(exportedMetrics)
        .Build();

    var engine = CreateEngine();

    // Act
    await engine.IndexItemAsync(CreateTestItem(), CancellationToken.None);
    meterProvider.ForceFlush();

    // Assert
    Assert.Contains(exportedMetrics, m => m.Name == "repoql.files.indexed");
    Assert.Contains(exportedMetrics, m => m.Name == "repoql.stage.duration");
    Assert.Contains(exportedMetrics, m => m.Name == "repoql.queue.depth");
}
```

### Load Tests

**Test: Metrics don't impact throughput**
```csharp
[Fact]
public async Task Metrics_DoNotDegradePerformance()
{
    // Arrange
    var items = Enumerable.Range(0, 1000).Select(_ => CreateTestItem()).ToList();

    // Act
    var withMetricsTime = await MeasureIndexingTime(items);

    // Assert
    // Expected: ~12-15 seconds for 1000 files (baseline established)
    // Acceptable overhead: < 2% (< 0.3 seconds)
}
```

---

## Implementation Phases

### Phase 1: Foundation (1-2 days)
**Goal**: Create static IndexingMetrics class with observable gauges

**Tasks**:
1. Create new `IndexingMetrics.cs` as static class
2. Define all 37 metrics (counters, histograms, observable gauges)
3. Add callback registration methods
4. Update `IndexingEngine` constructor to register callbacks
5. Add properties to `DocumentCatalog`, `EpochTracker`, `WorkQueue`
6. Verify in Aspire dashboard:
   - Queue depths appear
   - Workers active appears
   - Catalog entries appears

**Success criteria**: Observable gauges showing non-zero values

### Phase 2: Hot Path Counters (1 day)
**Goal**: Track files moving through pipeline stages

**Tasks**:
1. Add counter increments in `IndexingEngine.IndexItemAsync`:
   - Files enqueued, filtered, skipped
   - Files indexed, errored
2. Add counter increments in `IndexingEngine.ApplyIndexerPipeline`:
   - Files classified, parsed, enriched
3. Verify in Aspire dashboard:
   - Stage counters incrementing
   - Can drill down by mime_type

**Success criteria**: Stage progression visible, error rates visible

### Phase 3: Duration Histograms (1 day)
**Goal**: Measure stage performance

**Tasks**:
1. Add `Stopwatch` wrappers in `ApplyIndexerPipeline`:
   - Classification, parsing, enrichment stages
2. Add `Stopwatch` wrapper in `IndexItemAsync`:
   - Commit stage, hot path total
3. Verify in Aspire dashboard:
   - p50/p95/p99 latencies by stage
   - Can identify slow stages by mime_type

**Success criteria**: Duration histograms showing realistic values (5-50ms)

### Phase 4: Writer Metrics (1 day)
**Goal**: Track document lifecycle and graph extraction

**Tasks**:
1. Add counter increments in `SingleThreadedDatabaseWriter`:
   - Documents created/updated/deleted (in ApplyReplaceDocumentAsync, ApplyDeleteDocument)
   - Nodes/edges/spans extracted (in ApplyReplaceDocumentAsync)
   - Annotations upserted (in ApplyUpsertAnnotations)
2. Add transaction metrics (in ProcessOneAsync, TryProcessBatchAsync):
   - Transactions committed/failed
   - Batches committed
3. Verify in Aspire dashboard:
   - Document throughput visible
   - Graph extraction rates visible
   - Batch vs single-item distribution

**Success criteria**: Document creation rate matches file indexing rate

### Phase 5: Idle Processing Metrics (1 day)
**Goal**: Understand idle path performance

**Tasks**:
1. Add counter increments in `IndexingEngine`:
   - Idle cycles (in OnHotPathIdle)
   - Files pruned (in ReleaseAnalysisAsync)
   - Epochs completed (in CompleteEpochActivity)
2. Add histogram recordings:
   - Epoch size, epoch duration (in CompleteEpochActivity)
   - Idle phase durations (in ReleaseAnalysisAsync)
3. Verify in Aspire dashboard:
   - Epoch completion rate visible
   - Idle phase breakdown visible

**Success criteria**: Can identify idle processing bottlenecks

### Phase 6: Validation & Tuning (2-3 days)
**Goal**: Ensure metrics are accurate and useful

**Tasks**:
1. Load test with 10,000 files:
   - Verify performance overhead < 2%
   - Verify memory overhead < 10MB
2. Validate metric accuracy:
   - files.indexed = documents.created + documents.updated
   - Sum of stage durations ≈ hot_path.duration
3. Create Aspire dashboard panels
4. Document metrics in wiki
5. Add alerting thresholds

**Success criteria**: Metrics trusted by team, used for debugging

**Total estimated time**: 5-7 days

---

## Aspire Dashboard Configuration

### Panel Layout

**Overview Panel**:
```yaml
- Files/second (rate from files.indexed)
- Documents/second (rate from documents.created + documents.updated)
- Error rate (rate from files.errored / files.indexed)
- Queue utilization (queue.depth / queue.capacity)
```

**Stage Performance Panel**:
```yaml
- stage.duration p95 by stage (graph)
- stage.duration p50 by stage (graph)
- files.classified, files.parsed, files.enriched, files.indexed (stacked area)
```

**Writer Queue Health Panel** (Most important for monitoring writes):
```yaml
- queue.depth{queue=writer} (graph) - Is writer backlogged?
- queue.wait_time p95 (stat) - How long before processing?
- db.write.duration p95 (graph) - Is writing slow?
- transactions.committed rate (graph) - Write throughput
- transactions.failed rate (graph) - Are writes failing?
```

**Document Lifecycle Panel**:
```yaml
- documents.created rate (graph)
- documents.updated rate (graph)
- documents.deleted rate (graph)
```

**Graph Extraction Panel**:
```yaml
- nodes.extracted rate (graph)
- edges.extracted rate (graph)
- spans.extracted rate (graph)
```

**Idle Processing Panel**:
```yaml
- idle.phase.duration by phase (graph)
- epochs.completed rate (graph)
- epoch.size histogram (heatmap)
```

**Error Tracking Panel**:
```yaml
- files.errored rate by error_type (graph)
- transactions.failed rate by operation_type (graph)
- Recent errors (log link from exemplars)
```

### Alert Thresholds

| Metric | Threshold | Severity |
|--------|-----------|----------|
| Error rate | > 5% | Warning |
| Error rate | > 10% | Critical |
| Queue depth{queue=writer} | > 900 (90% of 1000) | Warning |
| Queue depth{queue=writer} | = 1000 (100%) | Critical |
| Queue wait time p95 | > 5 seconds | Warning |
| Stage duration p95 | > 10 seconds | Warning |
| Idle cycle duration | > 5 minutes | Warning |

---

## Querying Database Totals

Since database total metrics are not included in IndexingMetrics, use RepoQL queries:

```bash
# Document count
repoql query "SELECT COUNT(*) FROM node WHERE kind='document'"

# Node count
repoql query "SELECT COUNT(*) FROM node"

# Edge count
repoql query "SELECT COUNT(*) FROM edge"

# Annotation count
repoql query "SELECT COUNT(*) FROM annotation"

# Embedding count
repoql query "SELECT COUNT(*) FROM document_embedding"

# Full inventory with xray
repoql query "SELECT * FROM Files"
```

If you need these in Aspire dashboard later, create a separate **DatabaseMetricsCollector** background service:

```csharp
public class DatabaseMetricsCollector : BackgroundService
{
    private static readonly Meter _meter = new("RepoQL.Database");
    private readonly IDuckDbGraphStore _store;

    private long _documentsTotal;
    private long _nodesTotal;

    public DatabaseMetricsCollector(IDuckDbGraphStore store)
    {
        _store = store;

        _meter.CreateObservableGauge("repoql.db.documents.total",
            () => _documentsTotal, "documents");
        _meter.CreateObservableGauge("repoql.db.nodes.total",
            () => _nodesTotal, "nodes");
        // ... etc
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _documentsTotal = await QueryCountAsync("node WHERE kind='document'");
            _nodesTotal = await QueryCountAsync("node");

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }
}
```

**Benefits of separate service**:
- Separate meter ("RepoQL.Database", not "RepoQL.Indexing")
- Can disable independently
- Simple polling, no complex caching
- Zero coupling to hot path

---

## Migration Checklist

### Pre-Implementation

- [ ] Review design with team
- [ ] Get approval on metric names
- [ ] Confirm tag cardinality is acceptable
- [ ] Verify Aspire dashboard supports all metric types

### Implementation

- [ ] Phase 1: Foundation (observable gauges working)
- [ ] Phase 2: Hot path counters (stage progression visible)
- [ ] Phase 3: Duration histograms (performance visible)
- [ ] Phase 4: Writer metrics (document lifecycle visible)
- [ ] Phase 5: Idle processing (epoch tracking visible)
- [ ] Phase 6: Validation & tuning (metrics trusted)

### Validation

- [ ] Unit tests pass (counters increment correctly)
- [ ] Integration tests pass (metrics exported correctly)
- [ ] Load tests pass (overhead < 2%)
- [ ] Manual verification (Aspire dashboard shows expected values)

### Documentation

- [ ] Update metric catalog in wiki
- [ ] Create Aspire dashboard panels
- [ ] Write runbooks for common issues
- [ ] Document alert thresholds

### Rollout

- [ ] Deploy to dev environment
- [ ] Monitor for 24 hours
- [ ] Deploy to staging environment
- [ ] Monitor for 1 week
- [ ] Deploy to production
- [ ] Set up alerts

---

## Questions & Decisions Log

| Question | Decision | Rationale |
|----------|----------|-----------|
| Static or instance metrics? | Static | Single indexer per process, eliminates DI complexity |
| Include database total metrics? | No | Available via RepoQL query, adds 80 lines of complexity |
| Database count refresh frequency? | N/A (removed) | Query on-demand instead |
| Epoch tracking granularity? | Both gauge and histogram | Real-time monitoring + historical analysis |
| Tag cardinality limit for error_type? | Top 20 + "other" | Prevent unbounded cardinality |
| Use stateful throughput metrics? | No | Let OTel calculate from counters |

---

## Complexity Analysis

### Before (Current State)
- **IndexingMetrics**: Defined but unused (0 effective lines)
- **DI wiring**: None
- **Observability**: Broken (metrics always zero)

### After (This Design)
- **IndexingMetrics**: ~250 lines (static class with 37 metrics)
- **IndexingEngine**: +50 lines (metric increments + callback registration)
- **SingleThreadedDatabaseWriter**: +40 lines (metric increments)
- **Other components**: +18 lines (properties, helper methods)
- **Total added**: ~358 lines
- **DI wiring**: None (static)
- **Observability**: Complete (all stages visible)

### Comparison to Alternative Approaches

**Instance-based metrics (rejected)**:
- Would require +20 lines for DI registration
- Would require constructor parameter on 2 classes
- Would require passing instance through call stack
- Total: ~400 lines

**Database total caching (rejected)**:
- Would require +80 lines in DuckDbGraphStore
- Would add threading complexity (ConcurrentDictionary, SemaphoreSlim)
- Would add state management to database layer
- Total: ~450 lines

**Final design (selected)**:
- Static metrics: No DI wiring
- No database caching: Query on-demand
- Total: ~358 lines
- **Simplest approach** while maintaining full observability

---

**END OF DESIGN DOCUMENT**
