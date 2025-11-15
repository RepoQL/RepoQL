# Task 2: Add Backpressure + Pump for Watcher Enqueues

## Why
`RepoqlHost` currently dispatches file-system events via fire-and-forget tasks. Bursts create thousands of unawaited `_enqueue` calls, exceptions vanish, and cancellation/shutdown is racy. Introducing a bounded channel with a single consumer restores backpressure and makes shutdown deterministic.

## Plan
1. Add a bounded `Channel<RawArtifact>` plus a single pump task inside `RepoqlHost`. Configure `DropOldest` or coalescing to prevent unbounded growth.
2. Change the watcher observer to `TryWrite` into the channel rather than spawning tasks.
3. Start the pump alongside watchers; it should await `_enqueue` with the real host cancellation token.
4. On `StopAsync`, complete the channel writer, await the pump, then dispose the watcher subscriptions.
5. Add logging for dropped events to keep visibility.

## Pseudocode
```csharp
_channel = Channel.CreateBounded<RawArtifact>(new(...));
_pump = Task.Run(async () =>
{
    while (await _channel.Reader.WaitToReadAsync(ct))
        while (_channel.Reader.TryRead(out var artifact))
            await _enqueue(artifact, _options.DefaultIndexItemOptions, ct);
}, ct);

observer.OnNext(change)
{
    if (!_channel.Writer.TryWrite(ToArtifact(change)))
        _logger.LogWarning("Watcher queue full...");
}
```

## Definition of Done
- Watcher no longer spawns fire-and-forget tasks.
- Host shutdown waits for the pump and watcher without race conditions.
- Stress test (e.g., synthetic 100k changes) shows bounded memory and no lost cancellation.
