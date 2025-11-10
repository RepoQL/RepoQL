# Indexing Pipeline Reference

This document expands on `RepoQL.Indexing` internals so future maintainers can reason about stage transitions, state bits, and extension points without spelunking through the entire project.

## 1. Stage Contexts
Each pipeline stage is wrapped by `StageContext`, which accepts:

```csharp
new StageContext(
    busyFlag: IndexingState.ParsingBusy,
    idleFlag: IndexingState.ParsingIdle,
    processor: (item, ct) => Parser.ProcessItemAsync(item, ct));
```

- When `RunAsync` starts, the stage’s busy flag is OR’d into `IndexingEngine.State` and the idle flag is cleared.
- When the stage completes (success, filtered, or error), the busy flag is cleared and the idle flag set.
- Any stage entering busy will also set `IndexingState.Started`. `Started` is cleared only when all busy flags are zero (full hot path idle).

**Guidance**
- Only wrap long-lived, deterministic operations (classification, parsing, single-file analysis, multi-file analysis, index rebuild). Short operations (e.g., catalog lookups) should not toggle stage flags.
- When adding a new stage, register both busy and idle flags in `IndexingState` and extend `PipelineInvocationPlan` so tests can assert the expected behavior.

## 2. Catalog + Commit Interplay

1. `DocumentCatalog.Evaluate` returns `SkipUpToDate`, `Reindex`, or `Unknown`.
2. `BeginProcessing` registers the digest to prevent double work.
3. `IndexingCommitter` executes `WriteOperation`s via `IDatabaseWriter`. When the writer’s `OnCommitted` callback fires, call `DocumentCatalog.ApplyUpsert` / `ApplyDelete`.
4. On any failure (writer returns `CommitResult.Success == false`), throw so the stage reports `PipelineResult.Error`. This forces the caller to retry and avoids silent data loss.

**Guidance**
- Never swallow writer errors in enter/exit hooks. Let the exception propagate so telemetry captures the failure.
- `ApplyUpsert` should always clear pending state (`_pendingDigests`). If you introduce new catalog metadata, keep the same pattern: register before work, clear after commit.

## 3. Idle / Epoch Mechanics

### 3.1 Epoch Tracker
- `EnqueueItemAsync` stamps `IndexItem.SetEpoch(currentEpoch)` and increments the counter.
- `_epochTracker.Decrement(epoch)` returns `true` when the last item in that epoch finishes. When that happens *and* `State == IndexingState.AllIdle`, `HotPathIdle(epoch)` is raised.

### 3.2 Idle Queue
- `HotPathIdle` handlers should call `EnqueueIdleEpoch(epoch)` which writes to `_analysisEpochChannel`.
- `ProcessIdleEpochsAsync` is a single consumer that:
  1. Pulls the next epoch.
  2. Calls `ReleaseAnalysisAsync(epoch)` (pruner → writer delete → vector → analysis queue).
  3. Updates `_lastReleasedEpoch` to prevent duplicate work.

**Guidance**
- Keep idle handlers idempotent. If `HotPathIdle` fires twice for the same epoch (e.g., due to overlapping listeners), the channel handler will skip duplicates via `_lastReleasedEpoch`.
- Any new idle operation should be inserted inside `ReleaseAnalysisAsync` *after* pruner but *before* multi-file analyzer to maintain invariant: “analysis sees only current graph state.”

## 4. Post-Index Sequence

| Step | Implementation | Notes |
| --- | --- | --- |
| 1. Prune | `IArtifactPruner` | Works on the batch of pending `IndexItem`s. Should be fast; avoid hitting disk per file. |
| 2. Delete stale docs | `DeleteStaleDocumentsAsync` | Writes `WriteOperationType.DeleteDocument`. `OnCommitted` updates catalog. |
| 3. Vector refresh | `IVectorIndexCoordinator` | Always apply deletes before inserts/updates. Pair with `DuckDbVectorIndexRefresher` tests. |
| 4. Multi-file analysis | `MultiFileAnalysisPipeline` | Runs in parallel with `IndexRebuildPipeline`; keep processors side-effect free. |

**Guidance**
- Pruners should return canonical `RepoUri`s so vector/writer delete operate on the same identity.
- Vector refresh must serially wait for delete completion; this prevents stale embeddings.
- Multi-file analyzers must tolerate out-of-order batches (epochs are monotonically increasing but analysis may run after a newer epoch has started).

## 5. Events and Telemetry

| Event | When it fires | Intended consumer |
| --- | --- | --- |
| `StateChanged` | Any busy/idle flip | Observability / CLI waiters (`WaitForAsync`) |
| `HotPathIdle` | Last item in epoch drains and hot path idle | Idle orchestrator |

`IndexingMetrics` exposes counters (items processed, failures, queue depth); keep increments inside `IndexingEngine` and `IndexingCommitter`.

## 6. Testing Checklist
- Use `IndexingEngineTestFactory` to construct contexts with fakes or concrete dependencies.
- Assert catalog behavior with `CatalogInvocationPlan`.
- Assert pipeline activity with `PipelineInvocationPlan`.
- For idle/post-index tests, gate the parser stage and coordinate via `TaskCompletionSource` *only* when the exact sequence matters.
- For DuckDB-dependent tests, use `DuckDbTestStore` (no file system) and assert via `GraphAssertionHarness`.

## 7. Extension Points
- **New single-file analyzers**: extend `SingleFileAnalysisPipeline` constructor, register processors in DI, add harness tests.
- **New post-index operations**: insert after vector refresh, before multi-file, and provide tests verifying they run once per idle epoch.
- **State bits**: update `IndexingState`, `PipelineInvocationPlan`, and tests to keep observability consistent.

For deeper narrative explanations, read [`docs/IndexingProcess.md`](../../../../docs/IndexingProcess.md) and the knowledge capsules on format excellence.
