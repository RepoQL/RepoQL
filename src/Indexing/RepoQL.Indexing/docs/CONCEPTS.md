# RepoQL.Indexing Concepts

Semantic anchors for key concepts. Dense, retrievable, token-efficient.

---

### Capsule: **FlowObject** 📦 Pattern
IndexItem accumulates state through pipeline stages. Fields set by stages remain visible to later stages.

**Example**
```csharp
var item = new IndexItem(rawArtifact, options);
// item.MediaType == null

await classifier.ProcessAsync(item, ct);
// item.MediaType == "text/markdown.doc"

await parser.ProcessAsync(item, ct);
// item.Records != null (graph structure populated)
```

**Benefits**: Single object to inspect in debugger. Test any stage in isolation. Processors access previous results.

**Trade-offs**: Mutable state requires careful access control. Not thread-safe (mitigated by single-threaded stage execution).

---

### Capsule: **EpochTracking** ⏳ Coordination
Files enqueued together get same epoch number. When last item in epoch completes + hot path idle → `HotPathIdle(epoch)` fires.

**Example**
```csharp
var epoch = engine.BeginNewEpoch();  // Returns 42
await engine.EnqueueItemAsync(file1);  // epoch 42
await engine.EnqueueItemAsync(file2);  // epoch 42
await engine.EnqueueItemAsync(file3);  // epoch 42

// When file3 completes (last in epoch) + no other work active:
// HotPathIdle(epoch: 42) fires
```

**Benefits**: Batch post-processing (prune once, vector refresh once, multi-file analysis on complete set). Memory bounded (process by epoch, not all-at-once).

**Trade-offs**: Epoch completion tracking adds complexity. Long-running files delay entire epoch's post-processing.

---

### Capsule: **StageContext** 🎭 State
Wraps processor with automatic busy/idle flag management. Sets busy → calls processor → clears busy and sets idle (always, even on error).

**Pattern**
```csharp
var stage = new StageContext(
    IndexingState.ParsingBusy,
    IndexingState.ParsingIdle,
    (item, ct) => parser.ProcessItemAsync(item, ct)
);

await stage.RunAsync(item, ct, UpdateState);
// UpdateState called twice: once to set busy, once to set idle
```

**Benefits**: Never forget state updates. Consistent pattern. Safe error handling. Automatic telemetry.

**Implementation**: See StageContext.cs:7, StageContextExtensions.cs:23

---

### Capsule: **DocumentCatalog** 🗂️ Incremental
In-memory digest index. `Evaluate(uri, digest)` returns SkipUpToDate | Reindex | Unknown.

**Three-state model**:
```csharp
catalog.Evaluate("file:///test.md", "ABC123");
// Returns SkipUpToDate if digest matches committed state
// Returns Reindex if digest differs from committed state
// Returns Unknown if file never indexed
```

**Pending digests**: If file queued twice with same digest before first completes, second returns SkipUpToDate immediately (prevents duplicate work).

**Lifecycle**:
```csharp
1. Evaluate(uri, digest)  // Check if work needed
2. BeginProcessing(uri, digest)  // Register pending
3. [Pipeline runs]
4. OnCommitted → ApplyUpsert(entry)  // Update catalog after commit succeeds
```

**Implementation**: DocumentCatalog.cs:32

---

### Capsule: **HotPath** 🔥 Stages
Classification → Parsing → Single-file Analysis → Commit. Runs for every file change. Must be fast (concurrent, bounded queues).

**Characteristics**:
- Concurrent: ProcessorCount workers
- Per-file: Each file processes independently
- Fast: No expensive operations (embeddings, cross-file analysis)

**vs Idle**: Post-processing (multi-file, vector, pruning) runs once per batch after hot path drains.

---

### Capsule: **IdleProcessing** 💤 Sequence
Prune → Delete → Vector → Multi-file → Index Rebuild. Order matters for correctness.

**Sequence**:
```csharp
1. Prune: Identify deleted files (1 database query for batch)
2. Delete: Remove from database, update catalog
3. Vector: Refresh embeddings (batch operation, 50x faster)
4. Multi-file: Cross-reference analysis (sees complete graph)
5. Index Rebuild: Secondary indexes
```

**Why order matters**:
- Prune before vector: Don't compute embeddings for files about to be deleted
- Delete before analysis: Analysis shouldn't reference deleted files
- Vector before analysis: Some analyzers use embeddings

**Implementation**: IndexingEngine.ReleaseAnalysisAsync

---

### Capsule: **SerialWriter** ✍️ Thread
DatabaseWriter has exactly 1 worker thread. All database writes execute sequentially.

**Why**: DuckDB connections aren't thread-safe for concurrent writes. Options: (1) multiple writers + locks = slow/complex, (2) single writer = fast/simple.

**Trade-off**: Hot path can saturate writer. This is good—means parse is faster than write (CPU bound, not I/O bound). Writer catches up during idle periods.

**Enforcement**: Channel capacity: 1 in DatabaseWriter constructor. Tests assert single worker.

---

### Capsule: **OnCommitted** 🪝 Hook
Callback fires after database write succeeds. Used to update in-memory state (DocumentCatalog) after persistence confirmed.

**Pattern**
```csharp
new WriteOperation {
    Type: ReplaceDocument,
    Uri: item.Uri,
    ParsedData: records,
    OnCommitted: (op, result) => {
        if (result.Success)
            catalog.ApplyUpsert(new DocumentCatalogEntry(...));
    }
}
```

**Why callback**: Catalog must reflect committed state, not in-progress state. Fires only after database confirms write succeeded.

**Invariant**: Catalog updates ONLY via OnCommitted. Never update before commit.

---

### Capsule: **PipelineProcessor** 🔌 Extension
Implement `IAsyncPipeline<TIn, TOut>`. Register in DI. Runs automatically in appropriate stage.

**Convention**: Return null to skip. First processor to return non-null wins.

**Example**
```csharp
class CSharpClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?> {
    public Task<SemanticMediaType?> ProcessAsync(IDiscoveredArtifact a, CancellationToken ct) {
        if (a.Name.EndsWith(".cs"))
            return Task.FromResult(SemanticMediaType.Parse("text/x-csharp"));
        return Task.FromResult<SemanticMediaType?>(null);
    }
}

// Register
services.AddSingleton<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>, CSharpClassifier>();
```

**Stages**:
- Classification: `IDiscoveredArtifact → SemanticMediaType?`
- Parsing: `IClassifiedArtifact → Records?`
- Single-file Analysis: `IAnnotatedArtifact → PipelineResult`
- Multi-file Analysis: `IAnnotatedArtifact → PipelineResult`

---

### Capsule: **IndexingState** 🚦 Flags
Bit flags enum. Busy/Idle pairs per stage + composite flags.

**Flags**:
```csharp
ClassificationBusy, ClassificationIdle,
ParsingBusy, ParsingIdle,
SingleFileAnalysisBusy, SingleFileAnalysisIdle,
MultiFileAnalysisBusy, MultiFileAnalysisIdle,
IndexRebuildBusy, IndexRebuildIdle
```

**Composites**:
```csharp
Started = any busy flag set
AllIdle = all idle flags set
```

**Usage**:
```csharp
await engine.WaitForAsync(IndexingState.ParsingIdle, ct);
// Completes when ParsingIdle flag set

var state = engine.State;
if (state.HasFlag(IndexingState.Started)) { ... }
```

**Implementation**: IndexingState enum, IndexingEngine state management

---

### Capsule: **WorkQueue** 📋 Deduplication
Bounded work queue with deduplication. Same item can't be enqueued twice while pending or in-flight.

**Mechanism**:
```csharp
ConcurrentDictionary<T, byte> _waitSet;

public async ValueTask<bool> EnqueueAsync(T item, CancellationToken ct) {
    if (!_waitSet.TryAdd(item, 0))
        return false;  // Already queued or in-flight

    await _channel.Writer.WriteAsync(item, ct);
    return true;
}

// On completion:
_waitSet.TryRemove(item, out _);  // Allow re-enqueue
```

**Benefits**: Prevents duplicate work. Backpressure via bounded capacity.

**Configuration**: Capacity (bounded), workers (concurrency), equality comparer (deduplication key).

**Implementation**: WorkQueue.cs:12

---

### Capsule: **RawArtifact** 📄 Discovery
Wraps IFileInfo with lazy digest computation and provisional media type.

**Fields**:
```csharp
IFileInfo File;
AsyncLazy<byte[]> Digest;  // xxHash64 computed on demand
Lazy<SemanticMediaType?> ProvisionalMediaType;  // From extension
RepoUri Uri;
```

**Lazy evaluation**: Digest computed only when needed (e.g., catalog check). Avoids hashing files that will be filtered out.

**Implementation**: RawArtifact.cs:10

---

### Capsule: **Committer** 💾 Persistence
Converts IndexItem to WriteOperation. Sends to DatabaseWriter. Handles OnCommitted callback.

**Validation before commit**:
```csharp
- Records != null
- DigestHex != null
- MediaType != null
- Records contain document node
```

**Combines annotations**:
```csharp
var combinedAnnotations = [
    ...item.Records.Annotations,  // From parser
    ...item.AnnotationsList        // From analyzers
];
```

**Error handling**: If commit fails, throws exception. Allows retry.

**Implementation**: IndexingCommitter.cs:11

---

### Capsule: **RepoqlHost** 🏠 Lifecycle
IHostedService that runs indexing as background service.

**Responsibilities**:
- Enumerate filesystem on startup
- Subscribe to file system change notifications
- Enqueue changed files to IndexingEngine
- Manage service lifetime (start/stop/dispose)

**Integration**: Wired via IndexingServiceCollectionExtensions.AddRepoIndexer

---

### Capsule: **IndexingCoordinator** 🎯 Façade
User-facing API over IndexingEngine. Orchestrates reindex operations. Provides pipeline status.

**Key methods**:
```csharp
Task WaitForIdleAsync(CancellationToken ct);
Task WaitForPipelineAsync(stages, waitAll, ct);
PipelineStatusSnapshot GetPipelineStatus();
IAsyncEnumerable<ReindexProgressSnapshot> ReindexAsync(options, ct);
```

**Stages exposed**:
- Discovery (classification)
- Parsing (parsing + single-file analysis)
- Analysis (multi-file + index rebuild)
- Writer (database writer)

**Implementation**: IndexingCoordinator.cs:17

---

⟨CR-TAG:v1:8a4f⟩ Capsule: **FlowObject** 📦 Pattern
FlowObject

---

## ☑ Invariants

☑ Writer ALWAYS single-threaded (DuckDB safety)
☑ Catalog updates ONLY via OnCommitted (authoritative after commit)
☑ Epochs monotonically increasing (never reused)
☑ Pruner runs BEFORE vector refresh (don't embed deleted)
☑ Analysis sees ONLY committed graph (consistent state)

