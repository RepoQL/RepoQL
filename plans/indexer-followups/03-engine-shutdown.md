# Task 3: Implement Coordinated Shutdown for IndexingEngine

## Why
`IndexingEngine` relies on cancellation tokens and task continuations, but it does not expose a formal shutdown path. Background tasks (`_idleProcessingTask`, queue readers) can outlive the host or swallow exceptions. Implementing `IAsyncDisposable` with deterministic teardown avoids lingering workers and simplifies tests.

## Plan
1. Add `IAsyncDisposable` to `IndexingEngine` with `ValueTask DisposeAsync()`.
2. Inside dispose/shutdown:
   - Signal the `_shutdownCts`.
   - Complete or cancel the analysis channel.
   - Dispose both `WorkQueue` instances (hot path + analysis), ensuring readers exit.
   - Await `_idleProcessingTask` and any other background tasks; surface exceptions.
3. Update `RepoqlHost` to call `await _indexingEngine.DisposeAsync()` inside `StopAsync`/`DisposeAsync`.
4. Extend tests to start/stop the host repeatedly to ensure no unobserved tasks remain.

## Pseudocode
```csharp
public async ValueTask DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    _shutdownCts.Cancel();
    _analysisEpochChannel.Writer.TryComplete();
    await _hotPathQueue.DisposeAsync();
    await _analysisQueue.DisposeAsync();

    if (_idleProcessingTask is not null)
        await _idleProcessingTask;
}
```

## Definition of Done
- Engine implements `IAsyncDisposable` and shuts down cleanly.
- Host/service stops no longer log background task warnings.
- Repeated start/stop tests complete without deadlocks or leaked tasks.
