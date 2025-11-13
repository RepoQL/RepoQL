# RepoQL.Indexing

**Transform repository files into queryable graph database through staged discovery.**

This is the hot-path service that discovers what files are, materializes their structure, and orchestrates batch operations when the pipeline drains.

---

## Core Pattern

```mermaid
graph LR
    A["File Changed"] --> B["IndexItem"]
    B --> C["Classification"]
    C --> D["Parsing"]
    D --> E["Analysis"]
    E --> F["Commit"]
    F --> G["Epoch Complete"]
    G --> H["Idle Processing"]
    H --> I["Queryable"]

    style B fill:#e1f5ff
    style G fill:#ffe1e1

    %% MEANING: IndexItem is a flow object - it accumulates state through stages
    %% instead of being transformed. When all items in a batch (epoch) complete,
    %% idle processing runs once for the entire batch (prune/vector/multi-file).
```

### Capsule: **FlowObject** 📦 Discovery
IndexItem accumulates state through stages. Not transformed, *discovered*.

**Example**: `item.MediaType = null` → Classification → `item.MediaType = "text/markdown.doc"` → Parsing → `item.Records = {...}`

**Why**: Entire journey visible. Debug one object. Processors see full context.

### Capsule: **EpochTracking** ⏳ Batch
Files enqueued together share epoch number. Last completes + hot path idle → `HotPathIdle(epoch)` fires.

**Why**: Batch operations efficient. Prune once. Vector refresh once. Multi-file analysis on complete set.

### Capsule: **StageContext** 🎭 State
Wraps processor with automatic busy/idle flag management. Set busy → run processor → clear busy, set idle (always, even on error).

**Why**: Never forget state updates. Consistent telemetry. Safe error handling.

---

## Architecture

```
RepoqlHost (IHostedService)
├─ Scans filesystem on startup
├─ Watches for file changes
└─ Feeds IndexingCoordinator

IndexingCoordinator (Façade)
├─ Orchestrates reindex operations
├─ Provides pipeline status
└─ Exposes WaitFor APIs

IndexingEngine (Core)
├─ IndexerQueue (hot path, concurrent)
│   ├─ Classification Pipeline
│   ├─ Parsing Pipeline
│   ├─ Single-file Analysis Pipeline
│   └─ Committer → DatabaseWriter (serial)
├─ Epoch Tracker (batch coordination)
└─ AnalysisQueue (idle processing, concurrent)
    ├─ Pruner (find deleted)
    ├─ Vector Coordinator (refresh embeddings)
    ├─ Multi-file Analysis Pipeline
    └─ Index Rebuild Pipeline
```

**Threading Model**:
- **Hot path**: Concurrent (ProcessorCount workers) - parse multiple files in parallel
- **Writer**: Serial (1 worker) - DuckDB connections aren't thread-safe for writes
- **Idle processing**: Concurrent (ProcessorCount workers) - batch operations spawn many items

---

## Key Components

### IndexingEngine (IndexingEngine.cs:29)
Core pipeline orchestrator. Manages work queues, coordinates stages, tracks epochs, fires events.

**State flags**: Fine-grained busy/idle per stage (`ClassificationBusy`, `ParsingIdle`, etc.)

**Events**: `StateChanged`, `HotPathIdle`

### IndexItem (IndexItem.cs:15)
Flow object that accumulates state through pipeline. Acts as property bag for processors.

**Lifecycle**: `RawArtifact` → `+MediaType` → `+Records` → `+Annotations` → Committed

### DocumentCatalog (DocumentCatalog.cs:32)
In-memory digest index for incremental indexing.

**Decision**: `Evaluate(uri, digest)` → `SkipUpToDate` | `Reindex` | `Unknown`

### StageContext (StageContext.cs:7)
Stage wrapper with automatic state management.

**Pattern**: `new StageContext(busyFlag, idleFlag, processor)` → `RunAsync(item)` handles all transitions

### WorkQueue<T> (WorkQueue.cs:12)
Bounded, deduplicated work queue with backpressure.

**Deduplication**: Same item can't be enqueued twice while pending/in-flight

---

## Extension Points

### Adding a Processor

Implement `IAsyncPipeline<TIn, TOut>` and register in DI:

```csharp
// 1. Implement
class MyClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?> {
    public Task<SemanticMediaType?> ProcessAsync(IDiscoveredArtifact artifact, CancellationToken ct) {
        if (artifact.Name.EndsWith(".xyz"))
            return Task.FromResult(SemanticMediaType.Parse("application/x-xyz"));
        return Task.FromResult<SemanticMediaType?>(null);
    }
}

// 2. Register
services.AddSingleton<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>, MyClassifier>();
```

**Convention**: Return null to skip. First processor to return non-null wins.

**Stages**:
- **Classification**: `IDiscoveredArtifact → SemanticMediaType?`
- **Parsing**: `IClassifiedArtifact → Records?`
- **Single-file Analysis**: `IAnnotatedArtifact → PipelineResult`
- **Multi-file Analysis**: `IAnnotatedArtifact → PipelineResult`

See [PROCESSOR_GUIDE.md](PROCESSOR_GUIDE.md) for detailed guidance.

---

## State Observability

### IndexingState Flags

```csharp
[Flags]
enum IndexingState {
    // Busy flags
    ClassificationBusy, ParsingBusy, SingleFileAnalysisBusy,
    MultiFileAnalysisBusy, IndexRebuildBusy,

    // Idle flags (mirrors)
    ClassificationIdle, ParsingIdle, SingleFileAnalysisIdle,
    MultiFileAnalysisIdle, IndexRebuildIdle,

    // Composites
    Started = any busy flag set,
    AllIdle = all idle flags set
}
```

### Waiting for States

```csharp
// Wait for specific stage
await engine.WaitForAsync(IndexingState.ParsingIdle, ct);

// Wait for complete idle
await coordinator.WaitForIdleAsync(ct);

// Get current status
var status = coordinator.GetPipelineStatus();
Console.WriteLine($"Parsing active: {status.Stages[1].IsActive}");
Console.WriteLine($"Items queued: {status.Stages[1].QueuedCount}");
```

---

## Invariants

☑ **Writer ALWAYS single-threaded** - DuckDB write safety
☑ **Catalog updates ONLY via OnCommitted** - Authoritative after commit
☑ **Epochs monotonically increasing** - Never reused
☑ **Pruner runs BEFORE vector refresh** - Don't embed deleted docs
☑ **Analysis sees ONLY committed graph** - Consistent state

Break any invariant → subtle bugs. Maintain all → system works.

---

## Further Reading

- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** - Design principles and insights that led to this architecture
- **[docs/CONCEPTS.md](docs/CONCEPTS.md)** - Detailed capsules for all key concepts
- **[docs/JOURNEY.md](docs/JOURNEY.md)** - Complete trace of one file through the system
- **[docs/STATE-MACHINE.md](docs/STATE-MACHINE.md)** - Visual state diagrams with transitions
- **[docs/pipeline.md](docs/pipeline.md)** - Technical reference for stage mechanics
- **[PROCESSOR_GUIDE.md](PROCESSOR_GUIDE.md)** - How to add format processors
- **[AGENT_RULES.md](AGENT_RULES.md)** - Testing discipline and conventions

---

## Migration Context

This architecture replaced the monolithic `RepositoryIndexer` (RepoQL.Core, deleted).

**Old problems**: 2000+ line god class, hard-coded pipeline, tangled state, difficult testing.

**New solution**: Separated concerns (Host/Coordinator/Engine), composable pipelines, observable state, epoch-based batch coordination.

See `plans/indexer-migration/` for migration history.

---

**Philosophy**: Files don't get transformed through a pipeline—they're *discovered*. The IndexItem becomes what it needs to be, revealing itself one stage at a time. When a batch completes, post-processing happens once for the entire generation of work. This pattern makes the system debuggable, testable, and efficient.
