# RepoQL: Startup to Idle Flow

This document maps every step from RepoQL startup to reaching idle state. Understanding this flow is critical for reliability work.

## Overview: IndexingState Lifecycle

```mermaid
stateDiagram-v2
    [*] --> AllIdle: Engine created

    state "Hot Path Active" as HotPath {
        ClassificationBusy --> ParsingBusy: classified
        ParsingBusy --> SingleFileAnalysisBusy: parsed
        SingleFileAnalysisBusy --> CommitPending: analyzed
    }

    state "Idle Processing" as IdleProc {
        Pruning --> StructureEmbedding: pruned
        StructureEmbedding --> EmbeddingRefresh: embedded
        EmbeddingRefresh --> MultiFileAnalysis: refreshed
    }

    AllIdle --> HotPath: file enqueued
    HotPath --> IdleProc: epoch complete
    IdleProc --> AllIdle: analysis complete

    note right of HotPath
        Concurrent workers
        (ProcessorCount * 2)
    end note

    note right of IdleProc
        Sequential per epoch
        Triggered by HotPathIdle
    end note

    %% MEANING: IndexItem lifecycle through pipeline stages
    %% AllIdle = ready for work, HotPath = processing files, IdleProc = batch post-processing
```
*States: Gray = idle, Active = processing. Transitions show pipeline progression.*

---

## Phase 0: Initialization (Before RepoqlHost)

The host must complete significant initialization before `RepoqlHost.ExecuteAsync` runs.

```mermaid
flowchart TD
    subgraph Preflight["Preflight (ServeCommands.Serve)"]
        Start(["repoql serve"]) --> ShutdownExisting["Shutdown Existing Host"]
        ShutdownExisting --> AcquireLock["Acquire Host Lock"]:::action
        AcquireLock --> LockOk{Got lock?}
        LockOk -->|No| Exit(["Exit"]):::skip
        LockOk -->|Yes| WaitRepo["Wait for Repository"]
    end

    subgraph Services["Service Registration (AddRepoIndexer)"]
        WaitRepo --> RegFS["Register FileSystems"]
        RegFS --> RegEmbed["Configure Embedding Providers"]:::embed
        RegEmbed --> RegFormats["Register Format Loaders"]
        RegFormats --> RegDB["Register DuckDbDataStore"]:::db
        RegDB --> RegPipelines["Register Pipelines"]
        RegPipelines --> RegEngine["Register IndexingEngine"]
        RegEngine --> RegHost["Register RepoqlHost"]
    end

    subgraph DbInit["Database Init (DatabaseInitCoordinator)"]
        RegHost --> Prepare["Prepare: Validate Temp, Env Vars"]
        Prepare --> PrepFail{Valid?}
        PrepFail -->|No| ThrowPrep["Throw: Temp not writable"]:::error
        PrepFail -->|Yes| OpenDB["Open DuckDB Connection"]:::db

        OpenDB --> OpenFail{Success?}
        OpenFail -->|Yes| InitSchema["InitializeSchema()"]:::db
        OpenFail -->|No| ClassifyError{Error Type?}

        ClassifyError -->|Locked| TryKill["Try Kill Holder"]:::warn
        ClassifyError -->|Corrupted| Rebuild["Delete & Rebuild"]:::warn
        ClassifyError -->|Permission| ThrowPerm["Throw: Access Denied"]:::error
        ClassifyError -->|DiskFull| ThrowDisk["Throw: Disk Full"]:::error

        TryKill --> OpenDB
        Rebuild --> OpenDB
    end

    subgraph Schema["Schema Init (DuckDbDataStore.EnsureSchemaInternal)"]
        InitSchema --> ApplyConfig["Apply Connection Config"]
        ApplyConfig --> LoadVSS["Load VSS Extension"]:::embed
        LoadVSS --> CreateTables["Create Core Tables"]:::db
        CreateTables --> FormatScripts["Apply Format Scripts"]
        FormatScripts --> RegisterUDFs["Register UDFs"]
        RegisterUDFs --> SchemaVersion["Set Schema Version"]
    end

    subgraph AppStart["Application Startup"]
        SchemaVersion --> MapGrpc["Map gRPC Services"]
        MapGrpc --> SetHealth["Health: NOT_SERVING"]:::warn
        SetHealth --> StartHosted["Start Hosted Services"]
        StartHosted --> RepoqlHostStart(["RepoqlHost.ExecuteAsync"]):::success
    end

    classDef action fill:#81D4FA,stroke:#0277BD,color:#000
    classDef db fill:#A5D6A7,stroke:#388E3C,color:#000
    classDef embed fill:#CE93D8,stroke:#7B1FA2,color:#000
    classDef error fill:#FFB6C1,stroke:#C62828,color:#000
    classDef warn fill:#FFE082,stroke:#F57C00,color:#000
    classDef skip fill:#E0E0E0,stroke:#757575,color:#000
    classDef success fill:#90EE90,stroke:#2E7D32,color:#000

    %% MEANING: Full initialization before RepoqlHost starts
    %% COLOR: Blue=action, Green=database, Purple=embeddings, Red=fatal, Yellow=recoverable
```
*Colors: Blue = actions, Green = database ops, Purple = embeddings, Red = fatal errors, Yellow = recoverable*

### 0.1 Preflight (`ServeCommands.Serve`)
```
Location: ServeCommands.cs:40-68
```
1. **Shutdown existing host** - prevent write-write conflicts
2. **Acquire host lock** - file lock at `.repoql/host.lock`
3. **Wait for repository** - ensure filesystem is available

### 0.2 Service Registration (`AddRepoIndexer`)
```
Location: RepoIndexerServiceCollectionExtensions.cs:65-646
```

| Registration | Purpose |
|--------------|---------|
| `PhysicalFileSystem` | Primary repo filesystem |
| `DocumentationFileSystem` | Embedded `help://` scheme |
| `CompositeFileSystem` | Multi-mount aggregation |
| `IEmbeddingProvider` | OpenRouter cloud or local ONNX |
| `FormatDescriptor`s | Loaders for Markdown, C#, TypeScript, etc. |
| `DuckDbDataStore` | Core database with UDFs |
| `ClassificationPipeline` | File → MediaType |
| `ParsingPipeline` | File → Records |
| `SingleFileAnalysisPipeline` | Records → Annotations |
| `IndexingEngine` | Pipeline orchestrator |
| `RepoqlHost` | Background service |

### 0.3 Database Initialization (`DatabaseInitCoordinator`)
```
Location: DatabaseInitCoordinator.cs:50-143
```

| Error Type | Recovery |
|------------|----------|
| **Locked** | Try terminate holder process, retry |
| **Corrupted** | Delete `.duckdb` + `.wal`, recreate |
| **SchemaMismatch** | Delete and recreate |
| **Permission** | Fatal - throw |
| **DiskFull** | Fatal - throw |

### 0.4 Schema Initialization (`DuckDbDataStore.EnsureSchemaInternal`)
```
Location: DuckDbDataStore.cs:950-1060
```

1. **Connection config**: memory limit, threads, temp directory
2. **Load VSS extension**: vector similarity search
3. **Create core tables**:
   - `artifact` - file metadata, headline, structure
   - `node` - entities (documents, symbols, headings)
   - `edge` - relationships (contains, references, calls)
   - `span` - locations in files
   - `annotation` - lint, metrics, facts
   - `metadata` - schema version, assembly info
   - `embedding` - vector embeddings
4. **Apply format scripts**: format-specific views/macros
5. **Register UDFs**: `search()`, `snippet()`, `embed_text()`, etc.

### 0.5 Application Startup
```
Location: ServeCommands.cs:173-223
```
1. Build WebApplication
2. Map gRPC services (`RepoQlServiceImpl`, `HealthServiceImpl`)
3. Set health status to `NOT_SERVING`
4. Start hosted services:
   - `MountRestorationService` - restore persisted mounts
   - `CSharpWorkspaceHost` - Roslyn workspace
   - `McpHostedService` - MCP client connections
   - `RepoqlHost` - main indexing host
5. Wait for initial indexing → set health to `SERVING`

---

## Phase 1: Startup (`RepoqlHost.ExecuteAsync`)

```mermaid
flowchart TD
    Start(["ExecuteAsync"]) --> LogMounts["Log Mounted FileSystems"]

    LogMounts --> FullScan{RunFullScanOnStartup?}

    FullScan -->|Yes| DoScan["EnqueueFullScanAsync"]:::action
    FullScan -->|No| WatchCheck

    DoScan --> ScanFail{Success?}
    ScanFail -->|Yes| WatchCheck
    ScanFail -->|No| MarkDegraded1["Mark Degraded"]:::error
    MarkDegraded1 --> WatchCheck

    WatchCheck{EnableWatching?}

    WatchCheck -->|Yes| StartWatch["StartWatcherAsync"]:::action
    WatchCheck -->|No| SignalComplete

    StartWatch --> WatchFail{Success?}
    WatchFail -->|Yes| SignalComplete
    WatchFail -->|No| MarkDegraded2["Mark Degraded"]:::error
    MarkDegraded2 --> PollCheck{EnablePollingFallback?}

    PollCheck -->|Yes| EnablePoll["Enable Polling"]:::fallback
    PollCheck -->|No| SignalComplete

    EnablePoll --> SignalComplete

    SignalComplete["Signal _startupComplete"]:::success --> DirtyLoop["Start DirtyScanLoop"]

    DirtyLoop --> GitIndex["TriggerIncrementalGitIndexing"]:::action

    GitIndex --> GitFail{Success?}
    GitFail -->|Yes| Running(["Running"]):::success
    GitFail -->|No| LogWarn["Log Warning"]:::warn
    LogWarn --> Running

    classDef action fill:#81D4FA,stroke:#0277BD,color:#000
    classDef success fill:#90EE90,stroke:#2E7D32,color:#000
    classDef error fill:#FFB6C1,stroke:#C62828,color:#000
    classDef fallback fill:#FFE082,stroke:#F57C00,color:#000
    classDef warn fill:#FFF9C4,stroke:#F9A825,color:#000

    %% MEANING: Startup with failure handling and fallbacks
    %% COLOR: Blue=actions, Green=success, Red=error, Yellow=fallback/warn
```
*Colors: Blue = actions, Green = success, Red = degraded, Yellow = fallback/warning*

### 1.1 Mount Logging
```
Location: RepoqlHost.cs:88-95
```
- Iterates through all mounts in `CompositeFileSystem`
- Logs each mount's ID, scheme, and `includeInEnum` flag
- **Purpose**: Diagnostic visibility into which file systems are active

### 1.2 Full Scan (if `RunFullScanOnStartup = true`)
```
Location: RepoqlHost.cs:97-109 → EnqueueFullScanAsync()
```
1. Enumerate all files via `_fileSystem.EnumerateAsync()`
2. For each file:
   - Resolve to a mount via `_fileSystem.TryResolve(uri, out store)`
   - Skip non-existent files
   - Create `RawArtifact(file, store)`
   - Enqueue with `DefaultIndexItemOptions`
3. **On failure**: Log warning, mark degraded, continue with existing index

### 1.3 Watcher Initialization (if `EnableWatching = true`)
```
Location: RepoqlHost.cs:111-124 → StartWatcherAsync()
```
1. Create bounded channel for watcher events (capacity: 10,000 by default)
2. Start pump task: `PumpWatcherQueueAsync()`
3. Create composite watcher: `_fileSystem.WatchAll()`
4. Subscribe with `WatcherObserver`
5. Start watcher: `_watcher.StartAsync()`
6. **On failure**:
   - Log warning
   - Mark degraded
   - `EnablePollingFallback()` if configured

### 1.4 Startup Complete Signal
```
Location: RepoqlHost.cs:126
```
- `_startupComplete.TrySetResult()` signals that initial startup is done
- Callers can await `WaitForStartupAsync()` to know when setup is complete

### 1.5 Dirty Scan Loop Start
```
Location: RepoqlHost.cs:128
```
- Background task: `DirtyScanLoopAsync()`
- Handles:
  - Polling fallback (when watcher failed)
  - Dirty scans after watcher overflow
  - Periodic timer (every 1 second)

### 1.6 Git History Indexing
```
Location: RepoqlHost.cs:130-145 → IndexingCoordinator.TriggerIncrementalGitIndexingAsync()
```
1. Wait for pipeline to become idle: `WaitForIdleAsync()`
2. Find repo root: `RepoLocator.FindRepoRoot()`
3. Index git history: `_gitIndexer.IndexIncrementalAsync()`
4. **On failure**: Log warning, continue (git history is optional)

---

## Phase 2: Hot Path (`IndexingEngine.IndexItemAsync`)

### Entry Point
```
Location: IndexingEngine.cs:208-212 → EnqueueItemAsync()
```
1. Create `IndexItem(artifact, options)`
2. Assign epoch via `_epochTracker`
3. Add to `IndexerQueue` (bounded channel)
4. Increment epoch counter

### 2.1 Filter Check
```
Location: IndexingEngine.cs:458-468
```
- If `OnlyIfNotExcluded` flag set, check `Filter.IncludeFile(uri)`
- Skip files matching gitignore patterns
- Record as `Filtered` result

### 2.2-2.4 Hot Path Decision Flow

```mermaid
flowchart TD
    Entry(["File Enqueued"]) --> FilterCheck{Excluded by gitignore?}

    FilterCheck -->|Yes| Filtered["Skip (Filtered)"]:::skip
    FilterCheck -->|No| CatalogInit["Initialize Catalog"]

    CatalogInit --> ComputeDigest["Compute xxHash64"]
    ComputeDigest --> CatalogEval{Catalog Decision?}

    CatalogEval -->|SkipUpToDate| SkipUnchanged["Skip (Unchanged)"]:::skip
    CatalogEval -->|Unknown| BeginProc["BeginProcessing"]
    CatalogEval -->|Reindex| BeginProc

    BeginProc --> Classification["Classification Pipeline"]:::stage
    Classification --> Parsing["Parsing Pipeline"]:::stage
    Parsing --> ReadOnly{Read-only?}

    ReadOnly -->|Yes| SkipAnalysis["Skip Analysis"]:::skip
    ReadOnly -->|No| SingleFile["SingleFile Analysis"]:::stage

    SkipAnalysis --> Commit
    SingleFile --> Commit["Commit to DB"]:::commit

    Commit --> Schedule["Schedule for Idle Processing"]
    Schedule --> DecrEpoch["Decrement Epoch Counter"]

    DecrEpoch --> EpochDone{Last in epoch?}
    EpochDone -->|No| Done(["Complete"])
    EpochDone -->|Yes| CheckIdle{Engine idle?}

    CheckIdle -->|No| Done
    CheckIdle -->|Yes| FireEvent["Fire HotPathIdle"]:::event
    FireEvent --> Done

    classDef skip fill:#E0E0E0,stroke:#757575,color:#000
    classDef stage fill:#81D4FA,stroke:#0277BD,color:#000
    classDef commit fill:#90EE90,stroke:#2E7D32,color:#000
    classDef event fill:#FFE082,stroke:#F57C00,color:#000

    %% MEANING: Hot path decision points and branching
    %% COLOR: Blue=pipeline stages, Green=commit, Yellow=event, Gray=skip
```
*Colors: Blue = pipeline stages, Green = commit, Yellow = event trigger, Gray = skipped*

### Catalog Check Details
```
Location: IndexingEngine.cs:470-492
```
- `DocumentCatalog.EnsureInitializedAsync()` - hydrate catalog from DB on first call
- `DocumentCatalog.Evaluate(uri, digestHex)` compares digests
- `BeginProcessing()` prevents duplicate work if same file enqueued twice

### Pipeline Execution
```
Location: IndexingEngine.cs:952-1003 → ApplyIndexerPipeline()
```

#### Stage 1: Classification
```
Location: IndexingEngine.cs:959-970
ClassificationPipeline.ProcessItemAsync()
```
- **Input**: `IDiscoveredArtifact` (raw file info)
- **Output**: `SemanticMediaType` (e.g., `text/plain;kind=code.csharp`)
- **Processors**: Format classifiers (extension → media type mapping)
- **Updates**: `IndexItem.MediaType`

#### Stage 2: Parsing
```
Location: IndexingEngine.cs:972-983
ParsingPipeline.ProcessItemAsync()
```
- **Input**: `IClassifiedArtifact` (file + media type)
- **Output**: `Records` (nodes, edges, spans, annotations)
- **Processors**: Format parsers (C#, Markdown, JSON, etc.)
- **Updates**: `IndexItem.Records`

#### Stage 3: Single-File Analysis
```
Location: IndexingEngine.cs:991-1000
SingleFileAnalysisPipeline.ProcessItemAsync()
```
- **Input**: `IParsedArtifact` (file + records)
- **Output**: Additional annotations
- **Processors**: Analyzers (linting, metrics, etc.)
- **Updates**: `IndexItem.AnnotationsList`
- **Skip**: Read-only items (line 985-989)

### 2.5 Commit
```
Location: IndexingEngine.cs:508-511 → IndexingCommitter.CommitAsync()
```
1. Validate item has Records, DigestHex, MediaType, document node
2. Create `ParsedArtifact` from Records
3. Queue in batch (up to 64 items or 100ms timer)
4. `FlushPendingItems()`:
   - `_db.IndexArtifactBatch(items)` - write to DuckDB
   - `_catalog.ApplyUpsert(entry)` - update catalog
   - Complete waiting callers

### 2.6 Schedule Analysis
```
Location: IndexingEngine.cs:521 → ScheduleAnalysis()
```
1. Add to `_pendingStructureEmbeddings[epoch]` (all items)
2. Add to `_pendingAnalysis[epoch]` (non-read-only items only)
3. If epoch already released, re-enqueue for idle processing

### 2.7 Epoch Completion Check
```
Location: IndexingEngine.cs:561-563
```
- `_epochTracker.Decrement(epoch)` returns true when last item in epoch completes
- If epoch became idle AND engine state is `AllIdle`:
  - Fire `HotPathIdle` event

---

## Phase 3: Idle Processing

### Trigger: HotPathIdle Event
```
Location: IndexingEngine.cs:643-652 → OnHotPathIdle()
```
1. Complete epoch activity span
2. Record metrics
3. `EnqueueIdleEpoch(epoch)` - write to `_analysisEpochChannel`
4. Begin new epoch for subsequent work

### Processing Loop
```
Location: IndexingEngine.cs:711-757 → ProcessIdleEpochsAsync()
```
- Background task reading from `_analysisEpochChannel`
- For each epoch with pending work: `ReleaseAnalysisAsync(epoch)`

### 3.1 ReleaseAnalysisAsync - Idle Processing Flow
```
Location: IndexingEngine.cs:759-920
```

```mermaid
flowchart TD
    Start(["Epoch Complete"]) --> Extract["Extract Pending Items"]

    Extract --> HasWork{Has work?}
    HasWork -->|No| Done(["Return"])
    HasWork -->|Yes| IncrActive["Increment activeIdleProcessingCount"]

    IncrActive --> Prune["Prune Stale Documents"]:::prune

    Prune --> HasDeletes{Deletions?}
    HasDeletes -->|Yes| DeleteDB["Delete from DB"]:::prune
    HasDeletes -->|No| StructEmbed

    DeleteDB --> DeleteVec["Delete from Vector Index"]:::prune
    DeleteVec --> StructEmbed

    StructEmbed["Generate Structure Embeddings"]:::embed --> VecRefresh["Full-Text Vector Refresh"]:::embed
    VecRefresh --> VssRefresh["Rebuild VSS HNSW Indexes"]:::embed

    VssRefresh --> EnqueueAnalysis["Enqueue for Multi-File Analysis"]:::analysis

    subgraph AnalysisQueue["Analysis Queue (parallel workers)"]
        MultiFile["MultiFileAnalysisPipeline"]
        IndexRebuild["IndexRebuildPipeline"]
    end

    EnqueueAnalysis --> AnalysisQueue

    AnalysisQueue --> DecrActive["Decrement activeIdleProcessingCount"]
    DecrActive --> Done

    classDef prune fill:#FFB6C1,stroke:#C62828,color:#000
    classDef embed fill:#CE93D8,stroke:#7B1FA2,color:#000
    classDef analysis fill:#81D4FA,stroke:#0277BD,color:#000

    %% MEANING: Idle processing phases with parallel analysis at end
    %% COLOR: Red=pruning, Purple=embeddings, Blue=analysis
```
*Colors: Red = pruning, Purple = embedding generation, Blue = analysis*

#### Phase Details

| Phase | Location | Purpose |
|-------|----------|---------|
| Extract | :766-783 | Get pending items from epoch queues |
| Prune | :799-821 | Delete stale documents from DB and vectors |
| Structure Embed | :825-834 | Fast embeddings (URI + headline + structure) |
| Vector Refresh | :841-860 | Full-text embeddings with chunking |
| VSS Refresh | :863-872 | Rebuild in-memory HNSW indexes |
| Enqueue Analysis | :875-887 | Queue for cross-file analysis |

### 3.2 Analysis Queue Processing
```
Location: IndexingEngine.cs:1005-1026 → AnalyzeItemAsync()
```
- Runs in parallel:
  - `_multiFileStage.RunAsync()` - cross-file analysis (e.g., type resolution)
  - `_indexRebuildStage.RunAsync()` - index updates (e.g., FTS rebuild)

---

## Phase 4: Idle State

### State Flag Composition

```mermaid
flowchart LR
    subgraph HotPathFlags["Hot Path Flags"]
        C["ClassificationIdle"]
        P["ParsingIdle"]
        S["SingleFileAnalysisIdle"]
    end

    subgraph IdleFlags["Idle Processing Flags"]
        M["MultiFileAnalysisIdle"]
        R["IndexRebuildIdle"]
    end

    subgraph Queues["Queue Depths"]
        HQ["HotPath Queue = 0"]
        AQ["Analysis Queue = 0"]
        IPC["IdleProcessingCount = 0"]
    end

    HotPathFlags --> AllIdle{AllIdle?}
    IdleFlags --> AllIdle
    Queues --> AllIdle

    AllIdle -->|All true| Idle(["IDLE STATE"]):::success
    AllIdle -->|Any false| Busy(["BUSY"]):::busy

    classDef success fill:#90EE90,stroke:#2E7D32,color:#000
    classDef busy fill:#FFE082,stroke:#F57C00,color:#000

    %% MEANING: All conditions must be true for idle state
```
*Green = idle state reached, Yellow = still busy*

### Wait APIs
```
Location: IndexingCoordinator.cs:186-337
```

| API | Waits For | Timeout |
|-----|-----------|---------|
| `WaitForPipelineAsync(stages)` | Specific stages idle + queue empty | 1 minute |
| `WaitForIdleAsync()` | All stages: Discovery, Parsing, Analysis, Writer | 1 minute |

### Queue Depth Calculation per Stage
```
Location: IndexingCoordinator.cs:278-298
```

| Stage | Queue Depth Includes |
|-------|---------------------|
| Discovery | HotPath + MountIndexing |
| Parsing | HotPath + MountIndexing + PendingIdleProcessing |
| Analysis | HotPath + Analysis + MountIndexing + PendingIdleProcessing |
| Writer | HotPath + Analysis + MountIndexing + PendingIdleProcessing |

---

## Data Flow Summary

```mermaid
flowchart TD
    subgraph Input["Input"]
        File["File on Disk"]
    end

    subgraph HotPath["Hot Path (Concurrent)"]
        Raw["RawArtifact"]
        Digest["Digest + Catalog Check"]
        Class["Classification"]:::stage
        Parse["Parsing"]:::stage
        Analyze["SingleFile Analysis"]:::stage
        Commit["Commit to DuckDB"]:::commit
    end

    subgraph Batch["Epoch Batching"]
        Schedule["Schedule Analysis"]
        Wait["Wait for Epoch"]
    end

    subgraph Idle["Idle Processing (Sequential)"]
        Prune["Prune Stale"]:::prune
        StructEmbed["Structure Embeddings"]:::embed
        VecRefresh["Vector Refresh"]:::embed
        VssRebuild["VSS Index Rebuild"]:::embed
    end

    subgraph Analysis["Analysis (Concurrent)"]
        MultiFile["MultiFile Analysis"]:::analysis
        IndexRebuild["Index Rebuild"]:::analysis
    end

    File --> Raw --> Digest --> Class --> Parse --> Analyze --> Commit
    Commit --> Schedule --> Wait

    Wait -->|"HotPathIdle"| Prune --> StructEmbed --> VecRefresh --> VssRebuild

    VssRebuild --> MultiFile
    VssRebuild --> IndexRebuild

    MultiFile --> IdleState(["IDLE"]):::success
    IndexRebuild --> IdleState

    classDef stage fill:#81D4FA,stroke:#0277BD,color:#000
    classDef commit fill:#90EE90,stroke:#2E7D32,color:#000
    classDef prune fill:#FFB6C1,stroke:#C62828,color:#000
    classDef embed fill:#CE93D8,stroke:#7B1FA2,color:#000
    classDef analysis fill:#A5D6A7,stroke:#388E3C,color:#000
    classDef success fill:#C8E6C9,stroke:#1B5E20,color:#000

    %% MEANING: Complete data flow from file to idle state
    %% COLOR: Blue=hot path, Green=commit/analysis, Red=prune, Purple=embed
```
*Colors: Blue = hot path stages, Green = commit/analysis, Red = pruning, Purple = embeddings*

---

## Key Components

### Initialization Components

| Component | Purpose | Location |
|-----------|---------|----------|
| `ServeCommands` | CLI entry, preflight, app builder | `Commands/ServeCommands.cs` |
| `DatabaseInitCoordinator` | DB open, validation, recovery | `Host/DatabaseInitCoordinator.cs` |
| `DuckDbDataStore` | Schema init, UDF registration | `Data.DuckDB/DuckDbDataStore.cs` |
| `AddRepoIndexer` | DI registration for all services | `Core/RepoIndexerServiceCollectionExtensions.cs` |
| `HostLock` | File-based host lock | `Host/HostLock.cs` |

### Indexing Components

| Component | Purpose | Location |
|-----------|---------|----------|
| `RepoqlHost` | Background service orchestrating startup | `Hosting/RepoqlHost.cs` |
| `IndexingEngine` | Core pipeline orchestrator | `Indexing/IndexingEngine.cs` |
| `IndexingCoordinator` | High-level facade, status APIs | `Hosting/IndexingCoordinator.cs` |
| `ClassificationPipeline` | File → MediaType | `Pipelines/Classification/` |
| `ParsingPipeline` | File → Records | `Pipelines/Parsing/` |
| `SingleFileAnalysisPipeline` | Records → Annotations | `Pipelines/Analysis/` |
| `IndexingCommitter` | Batch writes to DuckDB | `Commit/IndexingCommitter.cs` |
| `DocumentCatalog` | Digest-based change detection | `State/DocumentCatalog.cs` |
| `EmbeddingCoordinator` | Embedding generation/refresh | `PostProcessing/EmbeddingCoordinator.cs` |
| `ArtifactPruner` | Stale document detection | `PostProcessing/StorageBackedArtifactPruner.cs` |
| `WorkQueue<T>` | Bounded channel with workers | (utility) |
| `EpochTracker` | Batch coordination | `IndexingEngine.cs` (inner class) |

---

## Failure Modes & Recovery

```mermaid
flowchart LR
    subgraph Init["Initialization Failures"]
        IF1["Host Lock Held"]:::error --> R0["Exit / Wait"]:::skip
        IF2["DB Locked"]:::error --> R0a["Kill holder, retry"]:::recovery
        IF3["DB Corrupted"]:::error --> R0b["Delete & recreate"]:::recovery
        IF4["Temp Dir Invalid"]:::error --> R0c["Fatal exit"]:::fatal
        IF5["Disk Full"]:::error --> R0d["Fatal exit"]:::fatal
    end

    subgraph Startup["Startup Failures"]
        SF1["Full Scan Fail"]:::error --> R1["Use existing index"]:::recovery
        SF2["Watcher Fail"]:::error --> R2["Polling fallback"]:::recovery
        SF3["ONNX Model Fail"]:::warn --> R2a["Hashed fallback"]:::degraded
    end

    subgraph Runtime["Runtime Failures"]
        RF1["Watcher Overflow"]:::warn --> R3["Dirty scan"]:::recovery
        RF2["File Parse Error"]:::error --> R4["Skip file"]:::skip
        RF3["Commit Batch Fail"]:::error --> R5["Caller exception"]:::skip
    end

    subgraph PostProcess["Post-Processing Failures"]
        PF1["Embedding Fail"]:::warn --> R6["Search degraded"]:::degraded
        PF2["VSS Refresh Fail"]:::warn --> R7["Vector search degraded"]:::degraded
        PF3["Git Index Fail"]:::warn --> R8["History unavailable"]:::degraded
    end

    classDef error fill:#FFB6C1,stroke:#C62828,color:#000
    classDef warn fill:#FFE082,stroke:#F57C00,color:#000
    classDef recovery fill:#90EE90,stroke:#2E7D32,color:#000
    classDef skip fill:#E0E0E0,stroke:#757575,color:#000
    classDef degraded fill:#FFF9C4,stroke:#F9A825,color:#000
    classDef fatal fill:#D32F2F,stroke:#B71C1C,color:#FFF

    %% MEANING: Failure types and their recovery strategies
    %% COLOR: Red=error, Yellow=warning, Green=recovery, Gray=skip, Light yellow=degraded, Dark red=fatal
```
*Colors: Red = error, Orange = warning, Green = recovery action, Gray = skipped, Light yellow = degraded, Dark red = fatal*

### Initialization Failures

| Failure Point | Current Behavior | Recovery |
|---------------|------------------|----------|
| Host lock held | Wait up to 45s | Exit if not acquired |
| DB locked by RepoQL process | Try terminate, retry | Fatal if can't kill |
| DB corrupted/schema mismatch | Delete `.duckdb` + `.wal` | Recreate from scratch |
| Temp directory invalid | Throw immediately | Fatal - must fix env |
| Disk full | Throw immediately | Fatal - free space |
| ONNX model load fails | Log warning | Use hashed fallback |
| MCP config fails | Log warning | MCP tools disabled |

### Runtime Failures

| Failure Point | Current Behavior | Recovery |
|---------------|------------------|----------|
| Full scan fails | Log warning, mark degraded | Existing index used |
| Watcher fails to start | Log warning, mark degraded | Polling fallback |
| Watcher overflow | Mark dirty flag | DirtyScanLoopAsync rescans |
| Git indexing fails | Log warning | Git history unavailable |
| Individual file parse error | Log error, skip file | File not indexed |
| Commit batch fails | All items in batch fail | Caller sees exception |
| Embedding generation fails | Log warning | Semantic search degraded |
| VSS index refresh fails | Log warning | Vector search degraded |

### Critical Pipeline Failure Modes

These failure modes can cause the pipeline to stall indefinitely. See [indexing-failure-modes.md](indexing-failure-modes.md) for detailed analysis.

| ID | Failure Mode | Severity | Detection |
|----|--------------|----------|-----------|
| FM-001 | Stuck item blocks pipeline (no per-item timeout) | Critical | Very Hard |
| FM-002 | Operations without timeouts (I/O, Roslyn, DB) | Critical | Hard |
| FM-003 | Epoch counter imbalance | High | Very Hard |
| FM-004 | WaitForIdleAsync blocks forever | Critical | Hard |
| FM-005 | Orphaned epoch items (race condition) | Critical | Medium |
| FM-006 | Worker attrition (no restart on exception) | Critical | Hard |
| FM-007 | ProcessIdleEpochsAsync silent death | Critical | Very Hard |
| FM-008 | Empty epoch skips pruning during reindex | High | Medium |
| FM-009 | Embedding provider failure causes item loss | High | Medium |
| FM-010 | Embedding starvation under continuous changes | High | Medium |

---

## Epoch Model

Epochs provide batch coordination:

1. **Creation**: `BeginNewEpoch()` starts a new epoch
2. **Assignment**: Each enqueued item gets current epoch number
3. **Tracking**: `EpochTracker` maintains pending count per epoch
4. **Completion**: When last item in epoch finishes and engine is idle → `HotPathIdle` fires
5. **Idle Processing**: Batch operations run for completed epoch
6. **New Epoch**: After `HotPathIdle`, new epoch begins for subsequent work

This ensures idle processing runs on complete batches, not individual files.

---

## Threading Model

| Component | Threading | Safety Mechanism |
|-----------|-----------|------------------|
| Hot path workers | Concurrent (ProcessorCount * 2) | Per-item isolation |
| Database writes | Serialized | `FlushLock` in IndexingCommitter |
| Analysis workers | Concurrent (ProcessorCount) | Per-item isolation |
| Idle processing | Single-threaded channel reader | Sequential epochs |
| Watcher pump | Single reader | Bounded channel |
| Dirty scan | Single task | Periodic timer |

**Critical Invariant**: Database writes are ALWAYS serialized through `IndexingCommitter.FlushLock` to prevent DuckDB corruption.
