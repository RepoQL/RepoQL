# Indexing Failure Modes

This document catalogs failure modes that can cause indexing to never complete. Each failure mode includes the root cause, detection difficulty, and potential mitigations.

## Status Summary (as of 2026-02-05)

| FM | Issue | Status |
|----|-------|--------|
| FM-001 | Stuck Item Blocks Pipeline | ✅ Mitigated |
| FM-002 | Operations Without Timeouts | ✅ Mitigated |
| FM-003 | Epoch Counter Imbalance | ⚠️ Low risk |
| FM-004 | WaitForIdleAsync Blocks Forever | ✅ Mitigated |
| FM-005 | Orphaned Epoch Items | ✅ Mitigated |
| FM-006 | Worker Attrition | ✅ Mitigated |
| FM-007 | ProcessIdleEpochsAsync Death | ✅ Mitigated |
| FM-008 | Empty Epoch Skips Pruning | ⚠️ Partial (logging only) |
| FM-009 | Embedding Failure Item Loss | ⚠️ Partial (items survive, no retry) |
| FM-010 | Embedding Starvation | ✅ Mitigated |

**Remaining work:**
- FM-008: Separate pruning from item processing to handle empty epochs during reindex
- FM-009: Implement retry queue for failed embedding epochs

---

## FM-001: Stuck Item Blocks Entire Pipeline ✅ MITIGATED

**Severity**: Critical → **Mitigated**
**Likelihood**: Unknown (no telemetry)
**Detection**: Now detectable via timeout metrics and logs

> **Resolution**: Per-item timeout implemented in `WorkQueue<T>`. Items exceeding the timeout
> (default 5 minutes for hot path, 10 minutes for analysis) are logged, their epoch counters
> decremented, and processing continues. Configure via `IndexingEngineOptions.HotPathItemTimeout`
> and `IndexingEngineOptions.AnalysisItemTimeout`.

### Description

If a single item hangs during processing, the entire indexing pipeline stalls indefinitely. There is no per-item timeout, no heartbeat tracking, and no automatic recovery.

### Root Cause

```csharp
// WorkQueue.cs:118 - worker loop has no timeout
await processItem(item, cancellationToken).ConfigureAwait(false);
```

The `CancellationToken` only fires on shutdown. Individual item processing has no timeout.

### Cascade Effect

1. **Worker hangs** → stuck in `processItem` forever
2. **Epoch counter never decrements** → `_epochTracker.Decrement()` never called
3. **HotPathIdle never fires** → requires epoch=0 AND AllIdle state
4. **State flags stay busy** → StageContext busy counter never decrements
5. **Idle processing never runs** → pruning, embeddings, analysis all blocked
6. **WaitForIdleAsync never returns** → callers wait forever

### Why Existing Timeout Doesn't Help

```csharp
// IndexingCoordinator.cs:261 - MaxQueueDrainWait = 1 minute
else if (stuckTimer.Elapsed > MaxQueueDrainWait)
```

This timeout only triggers when "workers are idle but queue has items". If a worker is **stuck** (busy), this branch never executes.

### Detection Gaps

| What We Track | What We Don't Track |
|---------------|---------------------|
| Queue depth | Per-item processing time |
| Active workers | Item start timestamp |
| Stage busy flags | Progress within item |
| Epoch counts | Stuck item identity |

### Potential Mitigations

1. **Per-item timeout**: Wrap `processItem` with `Task.WhenAny(processItem, Task.Delay(timeout))`
2. **Heartbeat tracking**: Record last progress timestamp per worker
3. **Stuck detection**: Background task that checks for workers idle > threshold
4. **Circuit breaker**: After N consecutive timeouts, skip problematic file patterns
5. **Diagnostic endpoint**: Expose currently-processing items with durations

### Questions to Answer

- What operations within `IndexItemAsync` could hang indefinitely?
- Are there I/O operations without timeouts?
- Can external processes (Roslyn, parsers) hang?
- What file types/sizes correlate with hangs?

---

## FM-002: Operations Without Timeouts Can Hang Indefinitely ✅ MITIGATED

**Severity**: Critical → **Mitigated**
**Likelihood**: Medium (depends on file types, network storage)
**Detection**: Now observable via per-operation timing and slow operation warnings

> **Resolution**: FM-001's per-item timeout (default 5 minutes) bounds all operations within
> `IndexItemAsync`. Additionally, per-operation timing metrics are now recorded for `catalog_init`
> and `digest` operations. Operations exceeding 30 seconds trigger warning logs to help identify
> bottlenecks before they reach the item timeout.

### Description

Within `IndexItemAsync`, several operations have no timeout. If any hangs, FM-001 cascades.

### Inventory of Operations in IndexItemAsync

| Operation | Location | Timeout | Risk |
|-----------|----------|---------|------|
| `DocumentCatalog.EnsureInitializedAsync()` | :471 | ❌ None | DB query on first call |
| `item.RawArtifact.Digest` | :472 | ❌ None | File read for hash |
| `Filter.IncludeFile()` | :458 | ❌ None | Gitignore check (fast) |
| **Classification Stage** | :960 | ❌ None | Usually fast |
| **Parsing Stage** | :974 | ⚠️ Mixed | See breakdown below |
| **SingleFile Analysis** | :993 | ❌ None | Analyzers |
| `Committer.CommitAsync()` | :509 | ❌ None | DB batch write |

### Parsing Stage Breakdown

| Parser | Timeout | Risk |
|--------|---------|------|
| TypeScript/JS | ✅ 30s | Node process call |
| C# Syntax | ❌ None | `CSharpSyntaxTree.ParseText()` - CPU bound |
| C# Project Load | ✅ 30s | `OpenProjectAsync()` |
| C# Compilation | ❌ None | `GetCompilationAsync()` - **dangerous** |
| C# Generators | ❌ None | `RunGeneratorsAsync()` |
| Markdown | ❌ None | CPU bound (fast) |
| JSON/YAML/etc | ❌ None | CPU bound |

### High-Risk Scenarios

#### Network Storage Hang
```
item.CreateReadStream() → network timeout
item.RawArtifact.Digest → stuck reading from SMB/NFS
```
No file I/O has timeouts. A mounted network drive going offline = hang.

#### Roslyn Compilation Hang
```csharp
// CSharpWorkspaceHost.cs:377 - NO TIMEOUT
var compilation = await project.GetCompilationAsync(cancellationToken);
```
Large projects or projects with many dependencies could take minutes. Source generators could loop infinitely.

#### Database Lock Hang
```csharp
// IndexingCommitter.cs:165 - FlushPendingItems holds _flushLock
lock (_flushLock)
{
    _db.IndexArtifactBatch(dbItems);  // ❌ No timeout on DB write
}
```
If database is locked by another process (corruption recovery, backup), writes hang.

### Cascade Effect

Any of these hangs → FM-001 (worker stuck) → entire pipeline stalls.

### Detection Gaps

- No per-operation duration metrics
- No "currently processing" diagnostic with start time
- No warning when operation exceeds threshold

### Potential Mitigations

1. **Wrap all external calls with timeout**:
   ```csharp
   await Task.WhenAny(
       operation,
       Task.Delay(timeout, cancellationToken)
   );
   ```

2. **Add operation-level tracing**:
   - Record start time for each operation
   - Emit warning if threshold exceeded
   - Expose "stuck operations" diagnostic

3. **File I/O timeout wrapper**:
   - Wrap `CreateReadStream()` with cancellation
   - Add read timeout for network filesystems

4. **Roslyn compilation timeout**:
   - Add timeout to `GetCompilationAsync`
   - Skip semantic analysis if too slow

---

## FM-003: Epoch Counter Imbalance Prevents Idle ⚠️ LOW RISK

**Severity**: High → **Low Risk**
**Likelihood**: Low (requires code bug)
**Detection**: Very Hard (counter mismatch not logged)

> **Status**: No explicit staleness detection implemented, but risk is significantly reduced by
> FM-001's per-item timeout (stuck items eventually complete) and FM-006's worker exception handling
> (workers no longer die silently). The main scenarios that would cause imbalance are now covered.

### Description

If `_epochTracker.Increment()` is called but `Decrement()` is never called (due to exception path or logic error), the epoch never completes.

### Root Cause

```csharp
// IndexingEngine.cs:226-227 - Increment on enqueue
_epochTracker.Increment(epoch);
incremented = true;

// IndexingEngine.cs:561 - Decrement in finally block of IndexItemAsync
var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
```

If an item is enqueued but never processed (queue corruption, worker death), the epoch is permanently stuck.

### Scenarios

1. **Worker task crashes without finally**:
   - Unlikely but possible with `ThreadAbortException` (deprecated)
   - Stack overflow in processItem

2. **Enqueue succeeds but item lost**:
   - Channel corruption (very unlikely)
   - Item dequeued but exception before `IndexItemAsync` runs

3. **Logic bug in early exit**:
   - Return before `finally` block executes (not possible in current code)

### Detection Gaps

- No periodic check for "stale epochs"
- No metric for epoch age
- No warning for epochs with items pending > threshold

### Potential Mitigations

1. **Epoch age monitoring**:
   - Track creation time of each epoch
   - Alert if epoch pending > 5 minutes with no progress

2. **Epoch item tracking**:
   - Keep list of items in each epoch
   - Log when epoch stalls with pending items

---

## FM-004: WaitForIdleAsync Blocks Forever When Worker Stuck ✅ MITIGATED

**Severity**: Critical → **Mitigated**
**Likelihood**: High (directly caused by FM-001)
**Detection**: Easy (caller never returns) but root cause hard

> **Resolution**: `WaitForStageCompleteAsync` now checks the timeout **inside** the polling loop,
> not after `WaitForAsync` returns. Progress detection resets the timer when queue depth changes.
> Additional check for `ActiveIdleProcessingCount > 0` handles idle processing scenarios.
> Polling interval prevents tight spinning while ensuring the timeout is always enforced.

### Description

When any worker is stuck processing an item, `WaitForIdleAsync()` blocks forever. The 1-minute timeout in `WaitForStageCompleteAsync` is never reached because it's checked **after** `WaitForAsync` returns, but `WaitForAsync` never returns.

### Root Cause

```csharp
// IndexingCoordinator.cs:224-276
private async Task WaitForStageCompleteAsync(...)
{
    while (true)
    {
        // Step 1: Wait for state flags - BLOCKS FOREVER
        await _engine.WaitForAsync(requiredState, cancellationToken);

        // Step 2: Check queue depth - NEVER REACHED
        var currentDepth = GetQueueDepthForStage(stage);

        // Step 3: Timeout check - NEVER REACHED
        if (stuckTimer.Elapsed > MaxQueueDrainWait)  // 1 minute
        {
            _logger.LogWarning("...");
            return;
        }
    }
}
```

### Why WaitForAsync Never Returns

```csharp
// IndexingEngine.cs:1030-1044
public async ValueTask<bool> WaitForAsync(IndexingState state, CancellationToken cancellationToken)
{
    while (true)
    {
        lock (_stateLock)
        {
            if (State.HasFlag(state))  // Requires idle flags
                return true;

            waitTask = _stateChangedTcs.Task;
        }

        await waitTask.WaitAsync(cancellationToken);  // Waits for state change
    }
}
```

| Condition | Value When Worker Stuck |
|-----------|-------------------------|
| `State` | `ClassificationBusy \| ...` |
| `requiredState` | `ClassificationIdle \| ...` |
| `State.HasFlag(requiredState)` | `false` (busy ≠ idle) |
| `_stateChangedTcs.Task` | Never completes |

State only changes when `UpdateStateFlags` is called → only happens when worker completes → worker is stuck → **infinite wait**.

### Call Chain

```
WaitForIdleAsync()
  └── WaitForPipelineAsync([Discovery, Parsing, Analysis, Writer])
        └── WaitForStageCompleteAsync(stage)
              └── _engine.WaitForAsync(requiredState)  ← STUCK
                    └── await _stateChangedTcs.Task    ← FOREVER
```

### Impact

All callers of `WaitForIdleAsync` hang:

| Caller | Location | Impact |
|--------|----------|--------|
| `TriggerIncrementalGitIndexingAsync` | IndexingCoordinator:127 | Git history never indexed |
| `ReindexAsync` | IndexingCoordinator:489 | Reindex never completes |
| `IndexMountAsync` | IndexingCoordinator:957 | Import never completes |
| Initial indexing barrier | ServeCommands:208 | Health never becomes SERVING |
| MCP `import` tool | waits for idle | Import hangs |

### The Timeout That Doesn't Help

```
┌─────────────────────────────────────────────────────────┐
│  MaxQueueDrainWait = 1 minute                           │
│                                                          │
│  Purpose: Timeout when "workers idle but queue full"    │
│                                                          │
│  Problem: Only checked AFTER WaitForAsync returns       │
│           But WaitForAsync never returns when busy      │
│                                                          │
│  Result: Timeout is dead code when worker is stuck      │
└─────────────────────────────────────────────────────────┘
```

### Detection Gaps

- No timeout on `WaitForAsync` itself
- No logging when wait exceeds threshold
- No metric for "time spent waiting for idle"
- Caller just hangs silently

### Potential Mitigations

1. **Add timeout to WaitForAsync**:
   ```csharp
   public async ValueTask<bool> WaitForAsync(
       IndexingState state,
       TimeSpan timeout,  // NEW
       CancellationToken cancellationToken)
   {
       using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
       cts.CancelAfter(timeout);
       // ... existing logic with cts.Token
   }
   ```

2. **Move timeout check inside the loop**:
   ```csharp
   while (true)
   {
       // Check timeout BEFORE waiting
       if (stuckTimer.Elapsed > MaxWait)
       {
           _logger.LogWarning("...");
           return;
       }

       // Wait with timeout, not indefinitely
       var completed = await Task.WhenAny(
           _engine.WaitForAsync(requiredState, cancellationToken),
           Task.Delay(pollInterval, cancellationToken)
       );
   }
   ```

3. **Periodic progress check**:
   - Poll state every N seconds instead of waiting indefinitely
   - Log warning if no progress after threshold
   - Eventually timeout and return partial success

---

## FM-005: Orphaned Epoch Items Block WaitForPipelineAsync ✅ MITIGATED

**Severity**: Critical → **Mitigated**
**Likelihood**: High (common during active development with file watcher)
**Detection**: Medium (pending count stays non-zero)

> **Resolution**: `OnHotPathIdle` now implements catch-up logic (mitigation #2). Instead of only
> enqueuing `args.Epoch`, it iterates over ALL epochs in `_pendingStructureEmbeddings.Keys` and
> enqueues each one. This handles the race condition where epoch N completes while N+1 is processing.
> Additionally, `ReleaseConsolidatedAnalysisAsync` collects items from ALL epochs in a batch.

### Description

When an epoch completes but the engine isn't fully idle (another epoch is processing), `HotPathIdle` doesn't fire. Items scheduled for idle processing remain in `_pendingStructureEmbeddings` forever. Since `GetPendingIdleProcessingCount()` includes these items, `WaitForPipelineAsync` never returns.

### The Race Condition

```
Timeline:
─────────────────────────────────────────────────────────────────────────
t0: Import starts, files enqueued to epoch N
t1: File watcher detects change, enqueues file to epoch N+1  ← NEW EPOCH
t2: Epoch N items complete, call ScheduleAnalysis()
    → Items added to _pendingStructureEmbeddings[N]
t3: Last item in epoch N completes
    → epochBecameIdle = true  ✓
    → State = ParsingBusy     ✗ (epoch N+1 still processing)
    → HotPathIdle DOES NOT FIRE FOR EPOCH N
t4: Epoch N+1 completes
    → HotPathIdle fires for N+1 only
    → ReleaseAnalysisAsync(N+1) runs
    → _pendingStructureEmbeddings[N] still exists!
t5: WaitForPipelineAsync checks GetPendingIdleProcessingCount()
    → Counts _pendingStructureEmbeddings[N] → returns > 0
    → Wait continues forever
─────────────────────────────────────────────────────────────────────────
```

### Root Cause

```csharp
// IndexingEngine.cs:562-563 - BOTH conditions required
if (epochBecameIdle && State == IndexingState.AllIdle)
    HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
```

| Condition | Epoch N Completes While N+1 Processing |
|-----------|----------------------------------------|
| `epochBecameIdle` | `true` - N is done |
| `State == AllIdle` | `false` - N+1 workers busy |
| Result | `HotPathIdle` skipped |

### No Catch-Up Mechanism

```csharp
// OnHotPathIdle only processes the TRIGGERING epoch
private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args)
{
    EnqueueIdleEpoch(args.Epoch);  // Only args.Epoch, not older epochs
}
```

When epoch N+1 fires `HotPathIdle`, it only enqueues epoch N+1. **Epoch N is never processed.**

### Items Never Removed

```csharp
// ReleaseAnalysisAsync:770-771 - only called for enqueued epochs
_pendingStructureEmbeddings.Remove(epoch, out structureEmbedQueue);
_pendingAnalysis.Remove(epoch, out analysisQueue);
```

Since epoch N is never enqueued, its items are never removed.

### Symptom

- Import completes (all files processed)
- `WaitForPipelineAsync` hangs
- Logs show "Waiting for pipeline stages..."
- `GetPendingIdleProcessingCount()` returns non-zero
- Diagnostic would show items in `_pendingStructureEmbeddings` for old epochs

### Trigger Conditions

1. Import running (epoch N)
2. File watcher or another import adds work (epoch N+1)
3. Epoch N completes before epoch N+1
4. Race condition: epoch N never fires `HotPathIdle`

### Potential Mitigations

1. **Fire HotPathIdle on epochBecameIdle alone** (simple but changes semantics):
   ```csharp
   if (epochBecameIdle)  // Remove AllIdle check
       HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
   ```

2. **Catch-up on any HotPathIdle**:
   ```csharp
   private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args)
   {
       // Process ALL epochs with pending work, not just this one
       lock (_analysisLock)
       {
           foreach (var epoch in _pendingStructureEmbeddings.Keys.ToList())
           {
               EnqueueIdleEpoch(epoch);
           }
       }
   }
   ```

3. **Periodic orphan detection**:
   - Background task checks for epochs with pending items
   - If epoch age > threshold and no activity, enqueue for processing

4. **Track epoch completion separately**:
   - Don't require `AllIdle` to process a completed epoch
   - Each epoch manages its own idle transition

---

## FM-006: Worker Attrition Through Unhandled Exceptions ✅ MITIGATED

**Severity**: Critical → **Mitigated**
**Likelihood**: Medium (depends on exception sources)
**Detection**: Medium (errors logged)

> **Resolution**: Worker-level try-catch added to `WorkQueue<T>` worker loop. Unhandled exceptions
> are logged but no longer kill the worker task. Processing continues with the next item.

### Description

When a worker encounters an unhandled exception (including exceptions thrown from `finally` blocks), the worker task dies permanently. There is no restart mechanism, no health monitoring, and no alerting. Over time, worker attrition degrades throughput until the pipeline stops completely.

### Root Cause

```csharp
// WorkQueue.cs:107-126 - Worker loop has no catch block
_readers = Enumerable.Range(0, _readerCount).Select(_ => Task.Run(async () =>
{
    await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
    {
        Interlocked.Increment(ref _busy);
        try
        {
            await processItem(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Complete(item);  // Item removed from queue
        }
        // ← NO CATCH - exception kills worker
    }
}, cancellationToken)).ToArray();
```

### Exception Sources That Kill Workers

1. **HotPathIdle event in finally block**:
   ```csharp
   // IndexingEngine.cs:562-563 - Inside finally block of IndexItemAsync
   if (epochBecameIdle && State == IndexingState.AllIdle)
       HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
   ```
   If `OnHotPathIdle` throws, the exception escapes the finally block and kills the worker.

2. **Activity/tracing operations**:
   ```csharp
   // IndexingEngine.cs:559-560 - Also in finally block
   Metrics?.RecordFileProcessed(...);
   AddEpochTag(item.Epoch, "index.result", status);
   ```

3. **Epoch tracker operations**:
   ```csharp
   // IndexingEngine.cs:561
   var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
   ```

### Cascade Effect

```
Worker 1 throws exception
    └── Worker 1 task faults
        └── _readers[0] = faulted Task
            └── No restart attempt
                └── N-1 workers remain
                    └── If all workers die → queue stops processing
                        └── HotPathIdle never fires
                            └── Idle processing never runs
```

### Worker Pool Characteristics

| Queue | Default Workers | Impact of Attrition |
|-------|-----------------|---------------------|
| IndexerQueue (hot path) | `ProcessorCount * 2` | Throughput degrades linearly |
| AnalysisQueue (idle processing) | `ProcessorCount` | Multi-file analysis slows |

### Detection Gaps

| What We Track | What We Don't Track |
|---------------|---------------------|
| `WorkersActive` gauge | Total healthy workers |
| Items processed | Worker death events |
| Queue depth | Worker fault reasons |

### Why Items Aren't Lost (But Pipeline Can Still Stall)

```csharp
// WorkQueue.cs:120-123 - finally always runs
finally
{
    Interlocked.Decrement(ref _busy);
    Complete(item);  // Removes from _waitSet
}
```

The item is marked complete even when the worker dies. But:
- The item was NOT successfully processed
- The epoch counter WAS decremented (in IndexItemAsync's finally)
- The worker is dead and won't process future items

### Potential Mitigations

1. **Add worker-level try-catch**:
   ```csharp
   await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
   {
       try
       {
           // ... existing logic
       }
       catch (Exception ex) when (ex is not OperationCanceledException)
       {
           _logger.LogError(ex, "Worker caught unhandled exception");
           // Continue processing - don't let worker die
       }
   }
   ```

2. **Worker health monitoring**:
   - Track `_readers` task states periodically
   - Log when workers fault
   - Expose "dead workers" metric

3. **Worker restart on fault**:
   ```csharp
   // Periodically check and restart dead workers
   for (int i = 0; i < _readers.Length; i++)
   {
       if (_readers[i].IsFaulted)
       {
           _readers[i] = Task.Run(WorkerLoop, cancellationToken);
       }
   }
   ```

4. **Protect finally block invocations**:
   ```csharp
   // IndexingEngine.cs - Wrap HotPathIdle invocation
   try
   {
       HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
   }
   catch (Exception ex)
   {
       Logger.LogError(ex, "HotPathIdle handler failed for epoch {Epoch}", item.Epoch);
   }
   ```

---

## FM-007: ProcessIdleEpochsAsync Silent Death ✅ MITIGATED

**Severity**: Critical → **Mitigated**
**Likelihood**: Low (requires early failure)
**Detection**: Very Hard → **Medium** (now logged on fault)

> **Resolution**: Eager exception observation implemented via `ContinueWith` with
> `TaskContinuationOptions.OnlyOnFaulted`. When the task faults, it logs immediately at
> `LogCritical` level rather than waiting for shutdown. The death is no longer silent.
> Note: Automatic restart and health metrics are not implemented - only logging.

### Description

The `ProcessIdleEpochsAsync` background task is started as fire-and-forget. If it throws an exception before entering its main loop, or if the loop exits unexpectedly, all idle processing stops permanently. Epochs continue to be enqueued to the channel, but no one reads them.

### Root Cause

```csharp
// IndexingEngine.cs:311 - Fire and forget
_idleProcessingTask = Task.Run(ProcessIdleEpochsAsync);
```

The task is stored but never observed during normal operation. Only `DisposeAsync` awaits it:

```csharp
// IndexingEngine.cs:416-423
if (_idleProcessingTask is not null)
{
    try
    {
        await _idleProcessingTask.ConfigureAwait(false);
    }
    catch (OperationCanceledException) { }
}
```

### What Could Cause Early Death

1. **Shutdown token already cancelled**:
   ```csharp
   // If Shutdown.Token is cancelled during construction
   while (await reader.WaitToReadAsync(Shutdown.Token).ConfigureAwait(false))
   // ^ Throws OperationCanceledException immediately
   ```

2. **Exception before loop entry**:
   ```csharp
   private async Task ProcessIdleEpochsAsync()
   {
       var reader = _analysisEpochChannel.Reader;  // Could theoretically throw
       try
       {
           while (await reader.WaitToReadAsync(...))  // First await - exception here = silent death
   ```

3. **Unhandled exception in ReleaseAnalysisAsync**:
   While exceptions in `ReleaseAnalysisAsync` are caught (line 747-750), any exception that escapes this catch would kill the task.

### Cascade Effect

```
ProcessIdleEpochsAsync dies
    └── _analysisEpochChannel has no readers
        └── OnHotPathIdle calls EnqueueIdleEpoch
            └── Epochs written to channel
                └── Nobody reads them
                    └── Pruning never runs
                    └── Embeddings never generated
                    └── Multi-file analysis never runs
                    └── WaitForPipelineAsync may hang (pending items never cleared)
```

### Detection Gaps

- No health check on `_idleProcessingTask`
- No logging when task dies (only on exception within the try block)
- No metric for "idle processor alive"
- Only discovered at shutdown when `DisposeAsync` awaits a faulted task

### Observable Symptoms

| Symptom | Explanation |
|---------|-------------|
| Hot path completes normally | Files are indexed and committed |
| Embeddings never generated | `VectorCoordinator` methods never called |
| Pruning never runs | Deleted files stay in database |
| `GetPendingIdleProcessingCount()` grows | Items scheduled but never processed |

### Potential Mitigations

1. **Add health monitoring**:
   ```csharp
   // Periodic check in a background timer
   if (_idleProcessingTask.IsFaulted)
   {
       Logger.LogCritical(_idleProcessingTask.Exception,
           "Idle processing task died unexpectedly");
       // Optionally restart
       _idleProcessingTask = Task.Run(ProcessIdleEpochsAsync);
   }
   ```

2. **Eager exception observation**:
   ```csharp
   _idleProcessingTask = Task.Run(ProcessIdleEpochsAsync);
   _ = _idleProcessingTask.ContinueWith(t =>
   {
       if (t.IsFaulted)
           Logger.LogCritical(t.Exception, "Idle processing task faulted");
   }, TaskContinuationOptions.OnlyOnFaulted);
   ```

3. **Expose task state as metric**:
   ```csharp
   meter.CreateObservableGauge("repoql.idle_processor.alive",
       () => _idleProcessingTask?.Status == TaskStatus.Running ? 1 : 0);
   ```

---

## FM-008: Empty Epoch Skips Pruning During Reindex ⚠️ PARTIAL

**Severity**: High
**Likelihood**: Low (requires all items to fail)
**Detection**: Medium → **Easy** (explicit warning log added)

> **Partial Mitigation**: Debug logging now explicitly warns when pruning is skipped due to
> empty epochs during reindex: "If this occurs during reindex, stale documents may not be pruned."
> However, the underlying issue remains - pruning still does not run for empty epochs.
> The recommended fix (separate pruning from item processing, track enumerated URIs) is not implemented.

### Description

During a reindex operation, if all items are filtered or error before reaching `ScheduleAnalysis`, the `_pendingStructureEmbeddings` queue is empty. `ReleaseAnalysisAsync` returns early before pruning runs. Deleted files that should be pruned remain in the database.

### Root Cause

```csharp
// IndexingEngine.cs:776-786 - Early exit if no work
var hasWork = (structureEmbedQueue is not null && structureEmbedQueue.Count > 0) ||
              (analysisQueue is not null && analysisQueue.Count > 0);
if (hasWork)
{
    Interlocked.Increment(ref _activeIdleProcessingCount);
    startedProcessing = true;
}

if (!startedProcessing)
    return;  // ← Pruning never runs!

// ... pruning code is below this point (line 797+)
```

### Pruner Dependency on pendingItems

```csharp
// StorageBackedArtifactPruner.cs:47-52
var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var item in pendingItems)  // ← If empty, live set is empty
{
    live.Add(item.Uri.AbsoluteUri);
}
```

The pruner compares the database against the "live" set. If `pendingItems` is empty:
- `live` set is empty
- Every document in DB is marked stale
- This would be catastrophic, BUT...

### Safety Net: IsReindexing Guard

```csharp
// StorageBackedArtifactPruner.cs:41-45
if (!_isReindexingAccessor())
{
    _logger.LogDebug("Pruning skipped because no reindex operation is active.");
    return Task.FromResult(PruningResult.None);
}
```

This prevents catastrophic deletion during normal operation. But during reindex:
- `IsReindexing = true`
- Empty `pendingItems` would mark everything as stale

### The Double Protection (and Its Gap)

| Scenario | IsReindexing | pendingItems | Pruning Behavior |
|----------|--------------|--------------|------------------|
| Normal watcher | `false` | varies | Skipped (guard) |
| Reindex, files succeed | `true` | populated | Works correctly |
| Reindex, all files error | `true` | empty | **Early return before pruning** |
| Reindex, all files filtered | `true` | empty | **Early return before pruning** |

The early return at line 786 prevents the catastrophic deletion, but also prevents legitimate pruning.

### When This Matters

Scenario: Corrupt repository
1. User runs reindex
2. All files fail to parse (corruption, encoding issues, etc.)
3. `pendingItems` is empty
4. `ReleaseAnalysisAsync` returns early
5. Previously-indexed (now-deleted) files remain in database
6. User fixes corruption, runs another reindex
7. **Old stale data still exists** (never pruned)

### Detection Gaps

- No warning when pruning is skipped due to empty items
- Pruning statistics would show 0 pruned (indistinguishable from "nothing to prune")
- No metric for "pruning skipped due to empty epoch"

### Potential Mitigations

1. **Log warning when empty during reindex**:
   ```csharp
   if (!startedProcessing && _isReindexingAccessor())
   {
       Logger.LogWarning(
           "Epoch {Epoch} has no items for idle processing during reindex. " +
           "Pruning will be skipped. This may leave stale documents in the database.",
           epoch);
   }
   ```

2. **Separate pruning from item processing**:
   - Run pruning unconditionally during reindex
   - Use a separate "seen files" set populated during hot path enumeration
   - Prune against enumeration, not successful processing

3. **Track enumerated vs processed separately**:
   ```csharp
   // In ReindexAsync, track what was enumerated
   var enumeratedUris = artifacts.Select(a => a.Uri).ToHashSet();

   // Pass to pruner directly, not through pendingItems
   await PruneAgainstEnumeratedAsync(enumeratedUris);
   ```

---

## FM-009: Embedding Provider Failure Causes Item Loss ⚠️ PARTIAL

**Severity**: High → **Medium**
**Likelihood**: Medium (network issues, API rate limits, provider outages)
**Detection**: Medium (error logged, but item loss is silent)

> **Partial Mitigation**: Embedding phases now have individual try-catch blocks in
> `ReleaseConsolidatedAnalysisAsync`. Structure embedding, vector refresh, and VSS index refresh
> are each wrapped separately. Multi-file analysis enqueue always runs regardless of embedding
> failures. Items are no longer completely lost - they proceed to analysis even if embeddings fail.
>
> **Remaining gap**: No retry queue for failed epochs. Embeddings that fail are simply missing
> until the file changes again. Circuit breaker and fallback embeddings are not implemented.

### Description

When the embedding provider fails during idle processing, items are permanently lost. They are removed from pending queues at the START of `ReleaseAnalysisAsync`, but if embedding generation throws, the multi-file analysis enqueue step is never reached. Items are neither in pending queues nor in the analysis queue.

### Root Cause

```csharp
// IndexingEngine.cs:770-771 - Items removed FIRST
lock (_analysisLock)
{
    _pendingStructureEmbeddings.Remove(epoch, out structureEmbedQueue);
    _pendingAnalysis.Remove(epoch, out analysisQueue);
    // ...
}

// IndexingEngine.cs:828 - Can throw
await VectorCoordinator.GenerateStructureEmbeddingsAsync(structureEmbedItems, ...);

// IndexingEngine.cs:853 - Can throw (rethrows from RefreshEmbeddingsAsync)
await VectorCoordinator.ApplyAsync(latest, ...);

// IndexingEngine.cs:880 - NEVER REACHED if above throws
await AnalysisQueue.EnqueueAsync(item, ...);

// IndexingEngine.cs:889 - Catches all, continues
catch (Exception ex) { ... }
```

### Embedding Provider Failure Points

| Provider | Failure Mode | Behavior |
|----------|--------------|----------|
| OpenRouterEmbeddingProvider | Network timeout (120s) | `TaskCanceledException` |
| OpenRouterEmbeddingProvider | Rate limit (429) | `HttpRequestException` thrown |
| OpenRouterEmbeddingProvider | Server error (5xx) | `HttpRequestException` thrown |
| OpenRouterEmbeddingProvider | Invalid response | `InvalidOperationException` |
| OnnxEmbeddingProvider | Model load failure | Exception during init |
| OnnxEmbeddingProvider | Inference failure | Native exception |
| Any provider | Out of memory | `OutOfMemoryException` |

### Partial vs Complete Failure

**Partial failure (individual batches):**
```csharp
// OpenRouterEmbeddingProvider.cs:168-173 - Batch failures are caught
catch (Exception ex)
{
    _logger.LogError(ex, "Error embedding batch {BatchIndex}...");
    // Array elements remain null - no exception propagated
}
```
Individual batch failures are silently handled - those items get null embeddings but processing continues.

**Complete failure (provider-level):**
```csharp
// VectorIndexCoordinator.cs:107-111 - Rethrows!
catch (Exception ex)
{
    _logger.LogError(ex, "Vector index refresh failed");
    throw;  // ← Propagates up
}
```
Provider-level failures propagate up and cause item loss.

### Cascade Effect

```
OpenRouter API returns 429 Rate Limited
    └── HttpRequestException thrown in CallApiAsync
        └── Not caught by ProcessSingleBatchAsync (timing issue or pre-batch failure)
            └── Propagates through EmbedBatchCoreAsync
                └── Caught by VectorIndexCoordinator.RefreshEmbeddingsAsync
                    └── Logged, then RETHROWN
                        └── ReleaseAnalysisAsync catch block (line 889)
                            └── Error logged, epoch marked as failure
                                └── Multi-file analysis NEVER runs
                                    └── Items were already removed from pending
                                        └── ITEMS LOST
```

### What Gets Lost

| What's Lost | Why |
|-------------|-----|
| Multi-file analysis | Never enqueued to AnalysisQueue |
| Cross-file type resolution | Depends on multi-file analysis |
| Index rebuild | Depends on multi-file analysis |
| Full-text embeddings | RefreshEmbeddingsAsync failed |
| Structure embeddings | GenerateStructureEmbeddingsAsync may have partially completed |

### What Still Works

| What Works | Why |
|------------|-----|
| Hot path indexing | Already committed to database |
| Pruning | Runs before embeddings (line 802) |
| VSS index refresh | Has internal try-catch (line 361-364) |
| Basic queries | File data is in database |

### Detection Gaps

- No metric for "items lost due to embedding failure"
- No retry queue for failed epochs
- No way to identify which items need re-processing
- Error is logged but item loss is silent

### OpenRouter-Specific Risks

```csharp
// OpenRouterEmbeddingProvider.cs:29-33
private const string Endpoint = "https://openrouter.ai/api/v1/embeddings";
private const int DefaultTimeoutSeconds = 120;
```

| Risk | Impact |
|------|--------|
| OpenRouter outage | All embedding requests fail |
| Rate limiting | Bursts of indexing trigger 429s |
| Network issues | Timeouts, connection failures |
| API changes | Response parsing fails |

### Potential Mitigations

1. **Don't remove items until processing completes**:
   ```csharp
   // Process first, then remove from pending
   var items = GetPendingItems(epoch);  // Copy, don't remove
   try {
       await GenerateStructureEmbeddingsAsync(...);
       await ApplyAsync(...);
       await EnqueueAnalysis(...);
       RemovePendingItems(epoch);  // Only on success
   } catch {
       // Items still in pending - will be retried
   }
   ```

2. **Retry queue for failed epochs**:
   ```csharp
   catch (Exception ex) {
       _failedEpochs.Enqueue(new FailedEpoch(epoch, items, ex));
       // Background task retries later
   }
   ```

3. **Circuit breaker for embedding provider**:
   ```csharp
   if (_embeddingFailureCount > Threshold) {
       _logger.LogWarning("Embedding provider circuit breaker open");
       // Skip embedding, continue with analysis
   }
   ```

4. **Fallback to hashed embeddings on provider failure**:
   ```csharp
   try {
       vectors = await _openRouterProvider.EmbedBatchAsync(...);
   } catch {
       _logger.LogWarning("Falling back to hashed embeddings");
       vectors = _hashedProvider.EmbedBatchSync(...);
   }
   ```

5. **Individual item error handling in structure embedding**:
   ```csharp
   // VectorIndexCoordinator.cs - Wrap batch processing
   foreach (var batch in batches) {
       try {
           await EmbedStructureBatchAsync(batch, ...);
       } catch (Exception ex) {
           _logger.LogWarning(ex, "Batch {N} failed, continuing...", batch.Index);
           // Continue with next batch, don't fail entire epoch
       }
   }
   ```

---

## FM-010: Embedding Starvation Under Continuous Changes ✅ MITIGATED

**Severity**: High → **Mitigated**
**Likelihood**: Medium (active development, auto-save, CI builds)
**Detection**: Observable via debug logs when epochs are consolidated

> **Resolution**: `ProcessIdleEpochsAsync` now drains ALL pending epochs from the channel
> before processing. Items from multiple epochs are consolidated into a single batch,
> ensuring we catch up during bursts instead of falling further behind. Debug logs show
> when epochs are being consolidated (e.g., "Consolidating 5 epochs (10-14)").

### Description

When file changes occur continuously, epochs queue up faster than embedding can process them. Since embedding doesn't set `State` busy flags, new epochs keep firing `HotPathIdle` even while previous epochs' embedding is running. The epoch queue grows unbounded, and embedding falls progressively further behind.

### Root Cause

```csharp
// IndexingEngine.cs:562 - HotPathIdle check
if (epochBecameIdle && State == IndexingState.AllIdle)
    HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
```

The `State` flags only include:
- ClassificationBusy/Idle
- ParsingBusy/Idle
- SingleFileAnalysisBusy/Idle
- MultiFileAnalysisBusy/Idle
- IndexRebuildBusy/Idle

**Embedding work in `ReleaseAnalysisAsync` does NOT set any busy flags.**

```csharp
// ReleaseAnalysisAsync:780 - Increments counter but NOT State
Interlocked.Increment(ref _activeIdleProcessingCount);
// ... embedding runs here ...
// State remains AllIdle during embedding!
```

### Timeline of Starvation

```
t0: Epoch 1 completes → HotPathIdle fires
t1: Epoch 2 begins, ReleaseAnalysisAsync(1) starts embedding
t2: File change → item added to epoch 2
t3: Epoch 2 item completes (fast, just 1 file)
t4: epochBecameIdle=true, State=AllIdle ← Embedding not tracked!
t5: HotPathIdle fires for epoch 2
t6: Epoch 3 begins, epoch 2 enqueued to channel
t7: Another file change → epoch 3
... repeat ...
t100: ReleaseAnalysisAsync(1) finally completes
t101: ProcessIdleEpochsAsync reads epoch 2 from channel
t102: ReleaseAnalysisAsync(2) starts
... meanwhile, epochs 3-100 are queued ...
```

### Epoch Queue Growth

```
ProcessIdleEpochsAsync (single-threaded):
    └── ReleaseAnalysisAsync(epoch 1) - SLOW (embedding)
         └── GenerateStructureEmbeddingsAsync - API calls
         └── RefreshEmbeddingsAsync - more API calls
         └── RefreshVssIndexAsync - rebuild indexes

Meanwhile, _analysisEpochChannel accumulates:
    [epoch 2] [epoch 3] [epoch 4] ... [epoch N]
```

### Consequences

| Consequence | Impact |
|-------------|--------|
| **Unbounded queue growth** | Memory pressure |
| **Pending items accumulate** | `_pendingStructureEmbeddings` grows |
| **Embedding latency increases** | N epochs behind = N * epoch_time delay |
| **Rate limiting risk** | OpenRouter 429 errors under load |
| **Never catches up** | If change rate > processing rate |

### When This Happens

| Scenario | Change Rate | Risk Level |
|----------|-------------|------------|
| Normal development | ~1-5 files/minute | Low |
| Auto-save enabled | ~10-30 files/minute | Medium |
| Large refactoring | ~100+ files/minute | High |
| CI/CD builds | Burst of all files | Critical |
| `git checkout` | All changed files | Critical |

### Why Hot Path Doesn't Have This Problem

Hot path has bounded queue (10,000 items) with backpressure:

```csharp
// WorkQueue uses BoundedChannelOptions with Wait
FullMode = BoundedChannelFullMode.Wait  // Blocks producer
```

But `_analysisEpochChannel` is **unbounded**:

```csharp
Channel.CreateUnbounded<long>(new UnboundedChannelOptions { ... })
```

### Detection

| Observable | Starvation Indicator |
|------------|---------------------|
| `_analysisEpochChannel` depth | Growing over time |
| `_pendingStructureEmbeddings.Count` | Accumulating |
| `EpochCurrent - lastProcessedEpoch` | Widening gap |
| Embedding API rate | Sustained high rate |

### Potential Mitigations

1. **Make embedding set a busy flag**:
   ```csharp
   // During ReleaseAnalysisAsync, set a flag that blocks HotPathIdle
   UpdateStateFlags(IndexingState.EmbeddingBusy, IndexingState.EmbeddingIdle, true);
   try {
       await GenerateStructureEmbeddingsAsync(...);
   } finally {
       UpdateStateFlags(IndexingState.EmbeddingBusy, IndexingState.EmbeddingIdle, false);
   }
   ```
   **Tradeoff**: New hot path items can't start idle processing until embedding completes.

2. **Batch epochs during embedding**:
   ```csharp
   // In OnHotPathIdle, don't start new epoch if embedding is active
   if (_activeIdleProcessingCount > 0)
   {
       // Add items to current epoch instead of starting new one
       return;
   }
   ```
   **Tradeoff**: Larger epochs, less granular progress.

3. **Bound the epoch channel**:
   ```csharp
   Channel.CreateBounded<long>(new BoundedChannelOptions(100) {
       FullMode = BoundedChannelFullMode.DropOldest
   });
   ```
   **Tradeoff**: Could drop epochs, requires consolidation logic.

4. **Debounce file changes**:
   ```csharp
   // In file watcher, batch rapid changes
   await Task.Delay(500);  // Wait for burst to complete
   EnqueueBatch(accumulatedChanges);
   ```
   **Tradeoff**: Latency for single-file changes.

5. **Consolidate pending epochs**:
   ```csharp
   // In ProcessIdleEpochsAsync, drain all pending epochs at once
   var allPendingEpochs = new List<long>();
   while (reader.TryRead(out var epoch))
       allPendingEpochs.Add(epoch);
   await ReleaseAnalysisAsync(allPendingEpochs);  // Process all together
   ```
   **Tradeoff**: Larger batches, but catches up faster.

6. **Skip embedding for stale epochs**:
   ```csharp
   // If epoch is more than N epochs behind, skip structure embeddings
   if (currentEpoch - epoch > 10)
   {
       Logger.LogWarning("Skipping structure embeddings for stale epoch {Epoch}", epoch);
       // Just enqueue for analysis, skip embedding
   }
   ```
   **Tradeoff**: Some items may lack embeddings until next quiet period.

---

## Telemetry Gap Analysis

This section documents what performance telemetry exists vs what's needed to identify bottlenecks.

### Metrics Actually Recorded

| Category | Metric | Tags | Location |
|----------|--------|------|----------|
| **Counters** | | | |
| Files | `FilesEnqueued` | mime_type, read_only | IndexingEngine:448 |
| Files | `FilesFiltered` | reason, mime_type | IndexingEngine:462 |
| Files | `FilesSkipped` | reason, mime_type | IndexingEngine:486 |
| Files | `FilesIndexed` | mime_type, status | IndexingEngine:514 |
| Files | `FilesErrored` | mime_type, error_type, stage | IndexingEngine:541 |
| Files | `FilesPruned` | - | IndexingEngine:809 |
| Files | `FilesClassified` | mime_type, result | IndexingEngine:964 |
| Files | `FilesParsed` | mime_type, result | IndexingEngine:977 |
| Files | `FilesEnriched` | mime_type, result | IndexingEngine:996 |
| Epochs | `IdleCycles` | - | IndexingEngine:648 |
| Epochs | `EpochsCompleted` | - | IndexingEngine:911 |
| **Histograms** | | | |
| Duration | `HotPathDuration` | status, mime_type, read_only | IndexingEngine:553 |
| Duration | `StageDuration` | stage, status, mime, read_only | IndexingEngine:592 |
| Duration | `IdlePhaseDuration` | phase | IndexingEngine:811,830,856,868,883 |
| Duration | `EpochDuration` | - | IndexingEngine:910 |
| Size | `EpochSize` | - | IndexingEngine:909 |
| Size | `FileSize` | mime_type, status | via RecordFileProcessed |
| **Gauges** | | | |
| Queues | `QueueDepth` | queue (indexer, analysis, writer) | via callbacks |
| Queues | `QueueCapacity` | queue | via callbacks |
| Queues | `WorkersActive` | queue (indexer, analysis) | via callbacks |
| Catalog | `CatalogEntries` | - | via callbacks |
| Catalog | `CatalogPending` | - | via callbacks |
| Epochs | `EpochCurrent` | - | via callbacks |
| Epochs | `EpochPendingItems` | - | via callbacks |

### Metrics Defined But NOT Recorded

| Metric | Purpose | Why Missing |
|--------|---------|-------------|
| `DocumentsCreated/Updated/Deleted` | Track DB mutations | Not instrumented in IndexingCommitter |
| `NodesExtracted/EdgesExtracted/SpansExtracted` | Track graph extraction | Not instrumented in parsers |
| `TransactionsCommitted/Failed` | Track DB health | Not instrumented in DuckDbDataStore |
| `BatchesCommitted` | Track commit batching | Not instrumented in IndexingCommitter |
| `DbWriteDuration` | DB write latency | Not instrumented |
| `BatchDuration` | Batch commit latency | Not instrumented |
| `QueueWaitTime` | Backpressure visibility | Not instrumented in WorkQueue |
| `EmbedRequests/Errors` | Embedding provider health | Not instrumented |
| `EmbedDuration` | Embedding latency | Not instrumented |
| `EmbeddingPhaseDuration` | Embedding breakdown | Not instrumented |
| `EmbeddingBatchSize` | Batch efficiency | Not instrumented |
| `DbTotals` | DB size by entity | Callback may not be registered |
| `DbConnectionsActive` | Connection pool health | Callback may not be registered |

### Critical Gaps for Bottleneck Detection

#### 1. Per-Parser Duration Breakdown

**Current State**: `StageDuration` with `stage=parsing` gives total time, but not per-parser.

**What's Missing**:
```
repoql.parser.duration{parser="csharp"} = 450ms
repoql.parser.duration{parser="typescript"} = 120ms
repoql.parser.duration{parser="markdown"} = 5ms
```

**Impact**: Can't identify slow parsers (e.g., Roslyn vs Markdig).

#### 2. File I/O vs Processing Time

**Current State**: `HotPathDuration` includes everything.

**What's Missing**:
```
repoql.io.read.duration{} = 50ms    # Time reading file content
repoql.io.digest.duration{} = 10ms  # Time computing hash
repoql.process.duration{} = 200ms   # Actual processing
```

**Impact**: Can't distinguish slow disk/network from slow processing.

#### 3. Embedding Provider Latency

**Current State**: `IdlePhaseDuration{phase="structure_embedding"}` gives total, but no breakdown.

**What's Missing**:
```
repoql.embed.api.duration{provider="openrouter"} = 2000ms
repoql.embed.api.batch_size{} = 100
repoql.embed.api.errors{error="rate_limit"} = 5
repoql.embed.api.retries{} = 3
```

**Impact**: Can't identify API rate limiting, network issues, or batch size tuning.

#### 4. Database Write Latency

**Current State**: No instrumentation.

**What's Missing**:
```
repoql.db.write.duration{operation="batch_insert"} = 150ms
repoql.db.write.rows{table="node"} = 500
repoql.db.lock.wait.duration{lock="flush_lock"} = 20ms
```

**Impact**: Can't identify DB as bottleneck, lock contention, or batch size issues.

#### 5. Queue Backpressure

**Current State**: `QueueDepth` shows current depth, but no wait time.

**What's Missing**:
```
repoql.queue.enqueue.wait{queue="indexer"} = 500ms  # Time blocked on full queue
repoql.queue.full.events{queue="indexer"} = 10      # How often queue is full
```

**Impact**: Can't identify when producers are blocked waiting for consumers.

#### 6. Worker Health

**Current State**: `WorkersActive` shows active count.

**What's Missing**:
```
repoql.workers.total{queue="indexer"} = 16      # Total workers created
repoql.workers.healthy{queue="indexer"} = 14   # Workers not faulted
repoql.workers.idle.duration{} = 50ms          # Time waiting for work
```

**Impact**: Can't detect worker attrition (FM-006) or underutilization.

#### 7. Memory Pressure

**Current State**: No instrumentation.

**What's Missing**:
```
repoql.memory.heap.bytes{} = 500MB
repoql.gc.collections{gen="2"} = 5
repoql.gc.duration{} = 200ms
```

**Impact**: Can't correlate performance issues with GC pressure.

#### 8. Item-Level Tracing

**Current State**: ActivitySource exists but spans may not correlate.

**What's Missing**:
- Trace ID propagation through all stages
- Per-item span with full lifecycle
- Parent-child relationships between stages

**Impact**: Can't trace a slow file through the entire pipeline.

### Recommendations

1. **Quick Wins** (low effort, high value):
   - Record `EmbedDuration` in OpenRouterEmbeddingProvider
   - Record `DbWriteDuration` in IndexingCommitter
   - Add `parser` tag to `StageDuration` for parsing stage

2. **Medium Effort**:
   - Add `QueueWaitTime` recording in WorkQueue.EnqueueAsync
   - Add worker health gauge (total vs faulted)
   - Add per-parser duration histograms

3. **Requires Design**:
   - Full distributed tracing with trace ID propagation
   - Memory/GC metrics integration with .NET diagnostics
   - Lock contention metrics

---

## Template for New Failure Modes

```markdown
## FM-XXX: [Title]

**Severity**: Critical | High | Medium | Low
**Likelihood**: High | Medium | Low | Unknown
**Detection**: Easy | Medium | Hard | Very Hard

### Description

[What happens]

### Root Cause

[Code location and explanation]

### Cascade Effect

[What else breaks as a result]

### Detection Gaps

[Why it's hard to notice]

### Potential Mitigations

[Possible fixes]
```
