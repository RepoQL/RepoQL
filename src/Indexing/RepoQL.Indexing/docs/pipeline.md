# Indexing Pipeline Reference

This document expands on `RepoQL.Indexing` internals so future maintainers can reason about stage transitions, state bits, and extension points without spelunking through the entire project.

## 1. Stage Contexts
Each pipeline stage is wrapped by `StageContext`, which accepts `(busyFlag, idleFlag, processor)` and records transitions through the central `UpdateStateFlags` helper. Rather than treating flags as mutable booleans, the engine keeps a counter per stage and recomputes `IndexingState` from those counters. Busy is set whenever the counter is > 0; idle otherwise; `Started` is set whenever any counter is busy.

**Guidance**
- Only wrap long-lived, deterministic operations (classification, parsing, single-file analysis, multi-file analysis, index rebuild). Short operations (e.g., catalog lookups) should not toggle stage flags.
- When adding a new stage, register the `(busyFlag, idleFlag)` pair via `RegisterStageCounter` so counters remain consistent and tests (see `PipelineInvocationPlan`) can assert behavior.

## 2. Catalog + Commit Interplay

1. `DocumentCatalog.Evaluate` returns `SkipUpToDate`, `Reindex`, or `Unknown`.
2. `BeginProcessing` registers the digest to prevent double work.
3. `IndexingCommitter` executes `WriteOperation`s via `IDatabaseWriter`. When the writer’s `OnCommitted` callback fires, call `DocumentCatalog.ApplyUpsert` / `ApplyDelete`.
4. On any failure (writer returns `CommitResult.Success == false`), throw so the stage reports `PipelineResult.Error`. This forces the caller to retry and avoids silent data loss.

**Guidance**
- Never swallow writer errors in enter/exit hooks. Let the exception propagate so telemetry captures the failure.
- `ApplyUpsert` should always clear pending state (`_pendingDigests`). If you introduce new catalog metadata, keep the same pattern: register before work, clear after commit.

## 3. Host Watcher + Backpressure

- `RepoqlHost` runs a bounded `Channel<RawArtifact>` for watcher events. The default capacity is `RepoqlHostOptions.WatcherQueueCapacity` (10k) and is configurable per deployment.
- File system events call `TryWrite`; when the channel is full the host logs `Watcher queue is full; dropping change for {Uri}` so drops are visible during bursts.
- A single pump task drains the channel and awaits `_enqueue` so backpressure propagates to the watcher; `_enqueue` failures are logged and the pump honors shutdown tokens.
- `StopAsync` sets `_isStopping`, completes the channel writer, awaits the pump, stops the watcher subscription, and (when the host owns the engine) awaits `IndexingEngine.DisposeAsync()` for deterministic teardown.

## 3. Idle / Epoch Mechanics

### 3.1 Epoch Tracker
- `EnqueueItemAsync` stamps `IndexItem.SetEpoch(currentEpoch)` and increments the counter.
- `_epochTracker.Decrement(epoch)` returns `true` when the last item in that epoch finishes. When that happens *and* `State == IndexingState.AllIdle`, `HotPathIdle(epoch)` is raised.

### 3.2 Idle Queue
- `HotPathIdle` handlers should call `EnqueueIdleEpoch(epoch)` which writes to `_analysisEpochChannel`.
- `ProcessIdleEpochsAsync` is now a single consumer *plus* bounded channel readers on `WorkQueue<T>`. Shutdown cancels the token, completes the channel, and awaits the pump task to ensure deterministic teardown.

**Guidance**
- Keep idle handlers idempotent. If `HotPathIdle` fires twice for the same epoch (e.g., due to overlapping listeners), the channel handler will skip duplicates via `_lastReleasedEpoch`.
- Any new idle operation should be inserted inside `ReleaseAnalysisAsync` *after* pruner but *before* multi-file analyzer to maintain invariant: “analysis sees only current graph state.”

## 4. Post-Index Sequence

| Step | Implementation | Notes |
| --- | --- | --- |
| 1. Prune | `IArtifactPruner` | Works on the batch of pending `IndexItem`s. Should be fast; avoid hitting disk per file. |
| 2. Delete stale docs | `DeleteStaleDocumentsAsync` | Writes `WriteOperationType.DeleteDocument`. `OnCommitted` updates catalog. |
| 3. Embedding refresh | `IEmbeddingCoordinator` | Always apply deletes before inserts/updates. `document_embedding` table no longer uses FK constraints; deletes must proactively clear rows. |
| 4. Multi-file analysis | `MultiFileAnalysisPipeline` | Runs in parallel with `IndexRebuildPipeline`; keep processors side-effect free. |

**Guidance**
- Pruners should return canonical `RepoUri`s so embedding refresh and writer delete operate on the same identity.
- Embedding refresh must serially wait for delete completion; this prevents stale embeddings.
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
- For DuckDB-dependent tests, use `DuckDbTestStore` (no file system) and assert via `GraphAssertionHarness`. Be mindful of the current schema (e.g., no FK constraints on `document_embedding`). Tests should verify the delete path clears derived tables explicitly.

## 7. Extension Points
- **New single-file analyzers**: extend `SingleFileAnalysisPipeline` constructor, register processors in DI, add harness tests.
- **New post-index operations**: insert after embedding refresh, before multi-file, and provide tests verifying they run once per idle epoch.
- **State bits**: update `IndexingState`, `PipelineInvocationPlan`, and tests to keep observability consistent.

For deeper narrative explanations, read [`docs/IndexingProcess.md`](../../../../docs/IndexingProcess.md) and the knowledge capsules on format excellence.
