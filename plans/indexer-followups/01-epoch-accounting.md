# Task 1: Fix Epoch Accounting When Deduplication Skips Enqueue

## Why
The indexing engine increments the epoch tracker before attempting to enqueue a work item. When the bounded queue rejects an item due to deduplication, the epoch count never balances, so `WaitForPipeline` can hang even though no work was scheduled. We need to increment only when the enqueue succeeds.

## Plan
1. Track the result of `WorkQueue.EnqueueAsync` in `IndexingEngine.EnqueueItemAsync` and only increment the epoch when it returns `true`.
2. Adjust error handling so failures still decrement the epoch if the increment already happened.
3. Add an explicit test that rapidly enqueues the same URI twice and ensures the epoch returns to zero after the dedup path.

## Pseudocode
```csharp
public async Task EnqueueItemAsync(RawArtifact artifact, ...)
{
    var epoch = _epochTracker.CurrentEpoch;
    var indexItem = new IndexItem(...);
    indexItem.SetEpoch(epoch);

    bool enqueued = await _queue.EnqueueAsync(indexItem, cancellationToken);
    if (enqueued)
    {
        _epochTracker.Increment(epoch);
    }
}
```

## Definition of Done
- Engine increments epochs only after successful enqueue.
- Unit test proves deduped items don’t leave the epoch tracker in-flight.
- No regressions in existing queue/epoch behavior (tests pass).
