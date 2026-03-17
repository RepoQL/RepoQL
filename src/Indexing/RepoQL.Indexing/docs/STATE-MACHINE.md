# IndexingState State Machine

Visual representation of state transitions with concrete conditions.

---

## State Flags

State is derived from per-stage counters captured in `IndexingEngine`. Each stage registers a `(busyFlag, idleFlag)` pair and the engine computes `IndexingState` based on whether the counter is greater than zero. Busy and idle are mutually exclusive per stage.

---

## Hot Path State Transitions

```mermaid
stateDiagram-v2
    [*] --> Idle: Engine starts

    Idle --> ClassificationBusy: EnqueueItem + StartStage
    ClassificationBusy --> ClassificationIdle: StageComplete

    ClassificationIdle --> ParsingBusy: StartStage
    ParsingBusy --> ParsingIdle: StageComplete

    ParsingIdle --> SingleFileAnalysisBusy: StartStage
    SingleFileAnalysisBusy --> SingleFileAnalysisIdle: StageComplete

    SingleFileAnalysisIdle --> Idle: Last item + no other work

    note right of ClassificationBusy
        Multiple items can be in this
        stage concurrently (ProcessorCount workers)
    end note

    note right of Idle
        Idle means: All busy flags clear
        AND all idle flags set
    end note

    %% MEANING: Each stage transitions busy→idle when StageContext.RunAsync completes.
    %% Multiple items can be at different stages simultaneously (concurrent processing).
    %% System returns to Idle when last item finishes AND no new items enqueued.
```

---

## Idle Processing State Transitions

```mermaid
stateDiagram-v2
    [*] --> WaitingForIdle

    WaitingForIdle --> PruningBusy: HotPathIdle(epoch) fires
    PruningBusy --> PruningIdle: Pruning complete

    PruningIdle --> DeletingBusy: Start deletes
    DeletingBusy --> DeletingIdle: Deletes complete

    DeletingIdle --> VectorBusy: Start vector refresh
    VectorBusy --> VectorIdle: Vectors computed

    VectorIdle --> MultiFileAnalysisBusy: Enqueue to AnalysisQueue
    VectorIdle --> IndexRebuildBusy: Enqueue to AnalysisQueue

    MultiFileAnalysisBusy --> MultiFileAnalysisIdle: Analysis complete
    IndexRebuildBusy --> IndexRebuildIdle: Rebuild complete

    MultiFileAnalysisIdle --> WaitingForIdle: All work done
    IndexRebuildIdle --> WaitingForIdle: All work done

    note right of PruningBusy
        Sequential per epoch
        (ReleaseAnalysisAsync runs once per epoch)
    end note

    note right of MultiFileAnalysisBusy
        Can run concurrently with IndexRebuildBusy
        (both use AnalysisQueue workers)
    end note

    %% MEANING: Idle processing triggered by HotPathIdle event.
    %% Sequence is prune→delete→vector→analysis (enforced by ReleaseAnalysisAsync).
    %% Analysis stages can overlap (concurrent workers on AnalysisQueue).
```

---

## Combined State Machine

```mermaid
stateDiagram-v2
    [*] --> AllIdle

    state "Hot Path Processing" as HotPath {
        [*] --> ClassificationBusy
        ClassificationBusy --> ClassificationIdle
        ClassificationIdle --> ParsingBusy
        ParsingBusy --> ParsingIdle
        ParsingIdle --> SingleFileAnalysisBusy
        SingleFileAnalysisBusy --> SingleFileAnalysisIdle
        SingleFileAnalysisIdle --> [*]
    }

    state "Idle Processing" as IdleProc {
        [*] --> Pruning
        Pruning --> Deleting
        Deleting --> EmbeddingRefresh
        EmbeddingRefresh --> MultiFile
        EmbeddingRefresh --> IndexRebuild
        MultiFile --> [*]
        IndexRebuild --> [*]
    }

    AllIdle --> HotPath: EnqueueItem
    HotPath --> AllIdle: Last item completes
    AllIdle --> IdleProc: HotPathIdle(epoch)
    IdleProc --> AllIdle: Idle work completes

    note right of HotPath
        Stages can overlap
        (worker pool processes multiple items)
    end note

    note right of IdleProc
        Sequential per epoch
        Multiple epochs can queue
    end note

    %% MEANING: System oscillates between HotPath and IdleProc.
    %% HotPath: Process files as they arrive (continuous).
    %% IdleProc: Batch operations when pipeline drains (periodic).
    %% AllIdle: No work active, waiting for next file or next idle epoch.
```

---

## State Transition Details

### StageContext.RunAsync Pattern

```csharp
public static async Task<PipelineResult> RunAsync(
    this StageContext stage,
    IndexItem item,
    CancellationToken cancellationToken,
    Action<IndexingState, IndexingState, bool> updateState)
{
    // Transition: → Busy
    updateState(stage.BusyFlag, stage.IdleFlag, entering: true);

    try {
        return await stage.Processor(item, cancellationToken);
    } finally {
        // Transition: Busy → Idle (always, even on error)
        updateState(stage.BusyFlag, stage.IdleFlag, entering: false);
    }
}
```

### UpdateState Implementation

            if ((State & BusyMask) == 0) {
                State &= ~IndexingState.Started;
            }
        }

        _stateChangedTcs.TrySetResult(true);  // Wake waiters
    }
}
```

**Note**: Counter-based tracking allows multiple items in same stage simultaneously.

---

## Concurrency Model

### Hot Path: Multiple Items, Multiple Workers

```mermaid
sequenceDiagram
    participant IQ as IndexerQueue
    participant W1 as Worker 1
    participant W2 as Worker 2
    participant W3 as Worker 3

    Note over IQ: 3 items enqueued

    IQ->>W1: Item A
    IQ->>W2: Item B
    IQ->>W3: Item C

    Note over W1,W3: All in Classification stage

    W1->>W1: Classification(A)
    W2->>W2: Classification(B)
    W3->>W3: Classification(C)

    W1->>W1: Parsing(A)
    W2->>W2: Parsing(B)
    W3->>W3: Parsing(C)

    Note over W1,W3: Overlapping stages

    %% MEANING: Workers process items independently.
    %% State flags track "any worker in this stage" (not per-item).
    %% Multiple items can be in same stage simultaneously.
```

**State during concurrent processing**:
```
Item A: In Parsing stage
Item B: In Classification stage
Item C: In Classification stage

State flags:
  ClassificationBusy = true (2 active)
  ClassificationIdle = false
  ParsingBusy = true (1 active)
  ParsingIdle = false
  Started = true
```

### Idle Processing: Sequential per Epoch

```mermaid
sequenceDiagram
    participant HPI as HotPathIdle Event
    participant CH as Channel
    participant IP as IdleProcessor

    Note over HPI: Epoch 42 completes
    HPI->>CH: Write(42)

    Note over HPI: Epoch 43 completes
    HPI->>CH: Write(43)

    IP->>CH: Read()
    CH-->>IP: 42
    IP->>IP: ReleaseAnalysisAsync(42)
    Note over IP: Prune→Delete→Vector→Analysis

    IP->>CH: Read()
    CH-->>IP: 43
    IP->>IP: ReleaseAnalysisAsync(43)
    Note over IP: Prune→Delete→Vector→Analysis

    %% MEANING: Single consumer processes epochs sequentially.
    %% Channel buffers epochs if idle processing slower than hot path.
    %% Within each epoch, operations run in fixed order.
```

---

## WaitForAsync Implementation

```csharp
public async ValueTask WaitForAsync(IndexingState targetState, CancellationToken ct) {
    while (true) {
        lock (_stateLock) {
            // Check if target state satisfied
            if ((State & targetState) == targetState) {
                return;
            }

            // Not satisfied, wait for state change
            _stateChangedTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await _stateChangedTcs.Task.WaitAsync(ct);
        // StateChanged event fired → check again
    }
}
```

**Usage examples**:
```csharp
// Wait for parsing to complete
await engine.WaitForAsync(IndexingState.ParsingIdle, ct);

// Wait for complete idle
await engine.WaitForAsync(IndexingState.AllIdle, ct);

// Wait for any of multiple flags
await engine.WaitForAsync(
    IndexingState.ParsingIdle | IndexingState.SingleFileAnalysisIdle,
    ct);
```

---

## Edge Cases

### Race Condition: Item Enqueued During Idle Check

```mermaid
sequenceDiagram
    participant E as Engine
    participant W as Waiter
    participant Q as Queue

    W->>E: WaitForAsync(AllIdle)
    E->>E: Check state: AllIdle = true
    Note over E: About to return...

    Q->>E: EnqueueItem (background thread)
    E->>E: State = Started

    E->>W: Returns (stale check)

    %% PROBLEM: Waiter thinks idle, but work just started
```

**Solution**: TaskCompletionSource + retry loop
```csharp
// Inside WaitForAsync loop
lock (_stateLock) {
    if ((State & targetState) == targetState) {
        return;  // Check inside lock
    }
    _stateChangedTcs = new TaskCompletionSource<bool>(...);
}
await _stateChangedTcs.Task;
// Loop back, check again
```

### Epoch Overflow

```csharp
private long _currentEpoch = 0;

public long BeginNewEpoch() {
    return Interlocked.Increment(ref _currentEpoch);
}
```

**Question**: What happens at `long.MaxValue` (9,223,372,036,854,775,807)?

**Answer**: System would need to process 1 billion epochs/second for 292 years to overflow. Practically impossible.

**If it happened**: Would wrap to negative numbers, break monotonicity, cause issues. Mitigation: Restart service long before reaching limit.

---

## Testing State Transitions

### Unit Test Example (IndexingEngineTests.cs)

```csharp
[Test]
[DisplayName("Skips unchanged artifacts when catalog confirms digest is current")]
public async Task Given_CatalogReportsUpToDate_When_IndexItemAsync_Then_SkipsProcessing()
{
    // Arrange
    var catalog = A.Fake<IDocumentCatalog>();
    A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._))
        .Returns(Task.CompletedTask);

    var existing = new DocumentCatalogEntry(
        CreateUri("file:///repo/already-indexed.md"),
        "A1B2C3",
        SemanticMediaType.Parse("text/markdown;kind=markdown.doc"),
        "C:\\repo\\already-indexed.md",
        DateTimeOffset.UtcNow.AddMinutes(-5));

    A.CallTo(() => catalog.Evaluate(A<RepoUri>._, A<string>._))
        .Returns(new DocumentCatalogEvaluation(DocumentCatalogDecision.SkipUpToDate, existing));

    var context = IndexingEngineTestFactory.Create(builder => builder.WithCatalog(catalog));

    var item = IndexingTestItemFactory.CreateIndexItem();

    // Act
    await context.Engine.IndexItemAsync(item, CancellationToken.None);

    // Assert
    catalog.ShouldMatch(item.Uri, CatalogInvocationPlan.SkipProcessing);
    item.ExistingEntry.Should().Be(existing);
}
```

### Integration Test Example (IndexerIntegrationTests.cs)

```csharp
[Test]
[Timeout(60_000)]
public async Task StartAndWaitForIdle_IndexesMarkdownDocument(CancellationToken token)
{
    var uri = RepoUri.Parse("embed:///Resources/Doc1.md");

    await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
    {
        options.Filter = new IncludeOnlyUriFilter(uri);
        options.EnableWatching = false;
        options.RunFullScanOnStartup = false;
        options.AdditionalMounts.Add(
            CompositeFileSystemMount.ForScheme(
                id: "embedded-docs",
                fileSystem: new EmbeddedStore(asm),
                scheme: "embed",
                includeInEnumeration: true));
    });

    await repo.IndexUriAsync(uri, skipUnchanged: false, token);

    // Assert indexed correctly
    var nodes = repo.Store.GetAllNodes().ToArray();
    nodes.Should().NotBeEmpty();
    nodes.Count(n => n.Kind == "document").Should().Be(1);
    nodes.Any(n => n.Kind == "md_heading").Should().BeTrue();
}
```

**Testing tools**:
- Unit tests: `FakeItEasy` for mocking, `AwesomeAssertions` for fluent assertions
- Factory helpers: `IndexingEngineTestFactory.Create()`, `IndexingTestItemFactory.CreateIndexItem()`
- Integration tests: `IndexedRepoBuilder.CreateAsync()` for full pipeline
- Custom assertions: `catalog.ShouldMatch()`, `PipelineInvocationPlan`

**See**: IndexingEngineTests.cs, IndexerIntegrationTests.cs for complete examples.

---

## Summary

**Key characteristics**:
- **Counter-based**: Track active count per stage (allows concurrency)
- **Lock-protected**: All state updates synchronized
- **Event-driven**: StateChanged event wakes waiters
- **Composite flags**: Started/AllIdle computed from individual flags
- **Mutex**: Busy and Idle mutually exclusive per stage

**Common patterns**:
- Check state → not satisfied → wait for event → check again (loop until satisfied)
- Enter stage → set busy, clear idle → process → clear busy, set idle (always in finally)
- Epoch tracking → count pending → last completes → fire event → idle processing

**Guarantees**:
- State transitions atomic (lock-protected)
- Idle flags accurate (counter reaches zero → flag set)
- Events fire exactly once per transition (TaskCompletionSource pattern)
- Waiters wake immediately (RunContinuationsAsynchronously)
