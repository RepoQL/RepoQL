# Indexing Flow

**Scope**: From `RepoqlHost.ExecuteAsync()` to idle state.

**Key files**:
- `file:///src/Indexing/RepoQL.Indexing/Hosting/RepoqlHost.cs` - File discovery, watching
- `file:///src/Indexing/RepoQL.Indexing/Hosting/IndexingCoordinator.cs` - Status facade
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs` - Pipeline orchestrator

---

## Critical Constraints

| Constraint | Violation Consequence |
|------------|----------------------|
| **Single-writer**: All DuckDB writes via `DuckDbDataStore` | Parallel writes corrupt database |
| **Catalog updates only via OnCommitted** | Stale cache causes duplicate work or data loss |
| **Epochs never reused** | Monotonically increasing; reuse breaks idle coordination |
| **Pruner before embeddings** | Embedding stale documents wastes compute |

---

## Capsule: HotPathFlow

**Invariant**
Each item flows sequentially through stages on one worker; multiple workers process different items concurrently.

**Example**
Worker picks item from `IndexerQueue`, runs all stages, returns item to queue's done set:
```
Filter → CatalogCheck → Classify → Parse → SingleFileAnalyze → Commit → ScheduleForIdle
```

**Depth**
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngineOptions.IndexingWorkers` controls parallelism (default: `ProcessorCount * 2`)
- Deduplication: `file:///src/Indexing/RepoQL.Indexing/WorkQueue.cs#symbol=WorkQueue._waitSet` prevents same URI being processed twice concurrently
- Stage state flags track how many workers are in each stage, not which items

---

## Capsule: CatalogGating

**Invariant**
DocumentCatalog compares file digest to skip unchanged files; pending set prevents duplicate in-flight work.

**Example**
`file:///src/Indexing/RepoQL.Indexing/Indexing/State/DocumentCatalog.cs#symbol=DocumentCatalog.Evaluate`:
```
Evaluate(uri, digest) →
  SkipUpToDate: digest matches committed entry
  Reindex: digest differs from committed entry
  Unknown: never seen before
```

**Depth**
- `file:///src/Indexing/RepoQL.Indexing/Indexing/State/DocumentCatalog.cs#symbol=DocumentCatalog.BeginProcessing` adds to pending set before pipeline
- `file:///src/Indexing/RepoQL.Indexing/Indexing/State/DocumentCatalog.cs#symbol=DocumentCatalog.ApplyUpsert` called only after successful DB commit
- Hydrates lazily from DB on first `EnsureInitializedAsync()` call

---

## Capsule: EpochBatching

**Invariant**
Items share an epoch number; when epoch's pending count hits zero and all stages idle, idle processing triggers.

**Example**
```
epoch = BeginNewEpoch()           // increments counter
EnqueueItemAsync(item)            // stamps epoch, increments pending[epoch]
... item completes ...
Decrement(epoch)                  // decrements pending[epoch]
  → if pending[epoch]==0 && State==AllIdle → HotPathIdle event
```

**Depth**
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=EpochTracker` maintains `_pendingByEpoch` dictionary
- Late arrivals: if `ScheduleAnalysis()` adds after epoch released, re-enqueues via `EnqueueIdleEpoch()`
- New epoch starts immediately after `HotPathIdle` fires

---

## Capsule: IdleProcessing

**Invariant**
After hot path drains, idle phase prunes deleted files, generates embeddings, then dispatches multi-file analysis.

**Example**
`file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngine.ReleaseAnalysisAsync`:
```
1. Prune    → delete stale docs from DB, notify VectorCoordinator
2. Embed    → VectorCoordinator.GenerateStructureEmbeddingsAsync (headline+structure → vector)
3. Refresh  → VectorCoordinator.ApplyAsync triggers full-text embedding refresh
4. Dispatch → enqueue items to AnalysisQueue
```

See `file:///src/Indexing/RepoQL.Indexing/Indexing/PostProcessing/VectorIndexCoordinator.cs` for embedding logic.

**Depth**
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngine._pendingStructureEmbeddings` includes read-only items (imports get embeddings)
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngine._pendingAnalysis` excludes read-only items (imports skip multi-file analysis)
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngine._activeIdleProcessingCount` tracks in-flight work for wait APIs

---

## Capsule: StateTracking

**Invariant**
Flags enum tracks active worker count per stage; AllIdle requires all five stage counters at zero.

**Example**
`file:///src/Indexing/RepoQL.Indexing/Indexing/StageContext.cs#symbol=StageContextExtensions.RunAsync`:
```
updateState(ClassificationBusy, ClassificationIdle, true)   // entering
... run processor ...
updateState(ClassificationBusy, ClassificationIdle, false)  // exiting
```

**Depth**
- Five stages: Classification, Parsing, SingleFileAnalysis, MultiFileAnalysis, IndexRebuild
- `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngine._stageCounters` tracks concurrent workers per stage
- `StateChanged` event + `_stateChangedTcs` signal waiters

---

## Pipeline Stages

| Stage | Output | Skipped When |
|-------|--------|--------------|
| Classification | `file:///src/Indexing/RepoQL.Indexing/Indexing/Pipelines/IndexItem.cs#symbol=IndexItem.MediaType` | Never |
| Parsing | `file:///src/Indexing/RepoQL.Indexing/Indexing/Pipelines/IndexItem.cs#symbol=IndexItem.Records` | Never |
| SingleFileAnalysis | `file:///src/Indexing/RepoQL.Indexing/Indexing/Pipelines/IndexItem.cs#symbol=IndexItem.AnnotationsList` | `IsReadOnly` |
| Commit | Persisted to DuckDB | Never |
| MultiFileAnalysis | Cross-file annotations | `IsReadOnly` |
| IndexRebuild | Index maintenance | `IsReadOnly` |

---

## Flow Diagram

```
RepoqlHost.ExecuteAsync()
  │
  ├─ EnqueueFullScanAsync()  ─┐
  └─ StartWatcherAsync()      │
                              ▼
                    IndexerQueue (N workers)
                              │
              ┌───────────────┴───────────────┐
              │      IndexItemAsync()         │
              │  Filter → Catalog → Classify  │
              │  → Parse → Analyze → Commit   │
              │         │                     │
              │    ScheduleAnalysis()         │
              └───────────────┬───────────────┘
                              │
           EpochTracker.Decrement() == 0?
                     AND State == AllIdle?
                              │ yes
                              ▼
              ┌───────────────────────────────┐
              │   ReleaseAnalysisAsync()      │
              │  1. Prune stale documents     │
              │  2. Structure embeddings      │
              │  3. Full-text refresh         │
              │  4. Enqueue to AnalysisQueue  │
              └───────────────┬───────────────┘
                              │
                    AnalysisQueue (N workers)
                              │
              ┌───────────────┴───────────────┐
              │  MultiFileAnalyzer (parallel) │
              │  IndexRebuilder    (parallel) │
              └───────────────┬───────────────┘
                              │
                              ▼
                      State == AllIdle
```

---

## Error Handling

| Error Location | Behavior |
|----------------|----------|
| Stage processor throws | Item logged, skipped; epoch continues |
| Commit batch fails | All items in batch get exception; catalog not updated |
| Embedding provider fails | Logged; items still dispatched to AnalysisQueue |
| Watcher buffer overflow | `_dirty` flag set; DirtyScanLoop re-enumerates |

---

## Non-Obvious Truths

| Gotcha | Why |
|--------|-----|
| Commit batches (64 items / 100ms) | Callers await `TaskCompletionSource` until batch flushes |
| ReadOnly gets embeddings, skips analysis | Imports need search but shouldn't trigger cross-file work |
| Catalog hydrates lazily | First item triggers DB load; subsequent items fast-path |
| Pruning order matters | Must delete from DB before embedding refresh sees stale data |
| Epochs can re-enqueue | Late `ScheduleAnalysis()` after release triggers re-processing |

---

## Wait APIs

| Method | Use When |
|--------|----------|
| `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngine.WaitForAsync` | Need specific state flags (low-level) |
| `file:///src/Indexing/RepoQL.Indexing/Hosting/IndexingCoordinator.cs#symbol=IndexingCoordinator.WaitForPipelineAsync` | Need specific stages complete |
| `file:///src/Indexing/RepoQL.Indexing/Hosting/IndexingCoordinator.cs#symbol=IndexingCoordinator.WaitForIdleAsync` | Need full quiescence |
| `file:///src/Indexing/RepoQL.Indexing/Hosting/RepoqlHost.cs#symbol=RepoqlHost.WaitForStartupAsync` | Need initial scan + watcher ready |

---

## Configuration

| Option | Default | Effect |
|--------|---------|--------|
| `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngineOptions.IndexingWorkers` | `ProcessorCount * 2` | Hot-path parallelism |
| `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngineOptions.AnalysisWorkers` | `ProcessorCount` | Idle-path parallelism |
| `file:///src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs#symbol=IndexingEngineOptions.IndexingQueueSize` | 10,000 | Backpressure threshold |
| `file:///src/Indexing/RepoQL.Indexing/Hosting/RepoqlHostOptions.cs#symbol=RepoqlHostOptions.RunFullScanOnStartup` | `true` | Enumerate all files at start |
| `file:///src/Indexing/RepoQL.Indexing/Hosting/RepoqlHostOptions.cs#symbol=RepoqlHostOptions.EnableWatching` | `true` | Watch for changes |
