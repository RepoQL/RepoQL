# Task 4: Derive IndexingState Flags from Per-Stage Counters

## Why
The coordinator maintains dictionaries of boolean flags to represent stage status. Maintaining separate booleans and counters drifts over time and complicates reasoning about `PipelineStatus`. Instead, each stage can expose a simple `activeCount` (pending + running); the state flags become a pure function of those counters.

## Plan
1. Introduce a `StageCounters` struct that tracks `Queued`, `Running`, and derives `IsBusy`/`IsIdle`.
2. Replace the per-stage dictionaries/maps inside `IndexingCoordinator` with `StageCounters` instances stored in a fixed array (one slot per enum value).
3. During enqueue/dequeue/complete, adjust counters under a lock, then recompute flags for `PipelineStatus` and `IndexingState` from the counters.
4. Update logging to include counts instead of boolean flags.
5. Adapt tests (or add new ones) to assert counters change as expected when the pipeline runs a few items.

## Pseudocode
```csharp
record struct StageCounters
{
    int queued;
    int running;
    public bool IsBusy => queued + running > 0;
}

void OnStageQueued(PipelineStage stage)
{
    lock (_stateLock)
    {
        _counters[stage].Queued++;
        RecomputeState();
    }
}
```

## Definition of Done
- `IndexingCoordinator` no longer stores duplicate boolean state.
- `PipelineStatus`/`WaitForPipeline` rely solely on counters.
- Tests demonstrate consistent behavior when stages queue/run/complete work.
