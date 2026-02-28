# .NET Concurrency Research

Research for all concurrency decisions in RepoQL — primitives, patterns, trade-offs, and pitfalls for a concurrent file-processing pipeline with single-writer database access, thread-safe in-memory registries, and long-lived gRPC host services.

*Research date: February 27, 2026*

## Context

RepoQL indexes repositories into a DuckDB knowledge graph. The system processes files concurrently through a multi-stage pipeline (classify, parse, analyze, commit), enforces single-writer access to DuckDB, maintains in-memory registries that must be thread-safe, coordinates epoch-based batching, and runs as a long-lived host process serving gRPC requests — all on a developer laptop alongside an IDE, browser, and LLM client.

This document is the authoritative reference for how .NET concurrency works, how RepoQL uses it, and what to watch for.

---

## How RepoQL Uses Concurrency Today

Before diving into primitives, here is the concurrency architecture as it exists in the codebase.

### The Pipeline

Two work queues drive all indexing:

| Queue | Workers | Capacity | Item Timeout | Purpose |
|-------|---------|----------|--------------|---------|
| Hot-path (IndexerQueue) | `ProcessorCount * 2` | 10,000 | 5 min | Classify → Parse → Analyze → Commit |
| Analysis (AnalysisQueue) | `ProcessorCount` | 100,000 | 10 min | Multi-file analysis, index rebuild |

Both are backed by `WorkQueue<T>`, which combines a bounded `Channel<T>`, a `ConcurrentDictionary` for deduplication, `Interlocked` counters for depth/busy tracking, and `Volatile` reads for idle detection via `TaskCompletionSource`.

> `src/Indexing/RepoQL.Indexing/WorkQueue.cs` — the core work-dispatch primitive
> `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs` — queue configuration and lifecycle

**Key design decision**: `IndexItem` is explicitly NOT thread-safe (stated at line 43 of `IndexItem.cs`). A single worker processes each item through all hot-path stages sequentially. Thread-safety is only needed for cross-worker operations: `TryMarkEpochComplete` and `TryMarkTimedOut` use `Interlocked.Exchange` because timeout handlers fire on different threads.

### The Database Gate

`DuckDbDataStore` enforces single-writer access through a custom reader-writer gate combining `SemaphoreSlim`, `Monitor.Wait/PulseAll`, and a connection pool:

- **Exclusive section** (writes): acquires `_readGateLock` monitor, waits while reads are active via `Monitor.Wait`, sets `_exclusiveOperationActive`, then acquires `SemaphoreSlim _lock`
- **Pooled reads** (untrusted queries from MCP clients): concurrent `DuckDBConnection` pool gated by `SemaphoreSlim _readPoolSlots`, blocked while exclusive operations are active
- **Reentrant reads** (UDFs querying during a query): `[ThreadStatic] _inQueryContext` detects reentrancy and routes to a separate read-only connection, avoiding SemaphoreSlim deadlock
- **DI scope flow**: `AsyncLocal<IServiceScope?>` flows DI scope to UDF callbacks

> `src/RepoQL.Data.DuckDB/DuckDbDataStore.cs` — lines 27-44 for field declarations, 241-309 for the gate

### The Registry

`UriRegistry` inherits from `ConcurrentDictionary<RepoUri, FileEntry>`. Thread safety comes from two properties:
1. `FileEntry` is an immutable record (updates via `with` expressions)
2. Atomic updates via `AddOrUpdate` and `TryUpdate` (optimistic concurrency — if a concurrent update changed the comparand, the update is silently dropped)

> `src/RepoQL.Contracts/UriRegistry/UriRegistry.cs`

### Channels Inventory

| Location | Type | Full Mode | Purpose |
|----------|------|-----------|---------|
| `WorkQueue<T>` (x2) | Bounded | Wait | Work dispatch with backpressure |
| `RepoqlHost._watcherChannel` | Bounded | DropOldest | File system change events (lossy OK) |
| `IndexingEngine._analysisEpochChannel` | Unbounded, SingleReader | N/A | Idle processing epoch dispatch |
| `IndexingEngine._structureEmbeddingChannel` | Bounded | Wait | Eager structure embedding |
| `StatusEventAggregator` subscribers | Bounded | DropOldest | Status broadcasting to gRPC streams (lossy OK) |
| `EmbeddingRefresher` | Bounded(2), SingleReader+SingleWriter | N/A | Double-buffered embedding pipeline |

Design pattern: `DropOldest` for non-critical event streams, `Wait` for work that must not be lost.

### Locking Inventory

**SemaphoreSlim** (16 instances):

| Instance | Init | Purpose |
|----------|------|---------|
| `DuckDbDataStore._lock` | (1,1) | Primary DB connection serialization |
| `DuckDbDataStore._readPoolSlots` | (N,N) | Read pool capacity |
| `VectorIndexCoordinator._refreshGate` | (N,N) | Embedding refresh concurrency limit |
| `VectorIndexCoordinator._vssRefreshSignal` | (0) | Signal-based wake for VSS worker |
| `DocumentCatalog._initializationGate` | (1,1) | One-time initialization |
| `RepoQlClient._connectLock` | (1,1) | Connection establishment |
| `CSharpWorkspaceHost` (x4) | Various | Roslyn compilation serialization |
| `TypeScriptNodeClient._mutex` | (1,1) | Node.js process serialization |

**lock (Monitor)** (~60 instances): `_stateLock` (state machine), `_analysisLock` (pending analysis), `_batchLock`/`_flushLock` (commit batching), `_readGateLock` (reader-writer coordination with `Monitor.Wait/PulseAll`).

**Interlocked** (~80 instances): Counters (depth, busy, epoch), one-shot flags (`Exchange`), CAS state transitions (`CompareExchange`), 64-bit reads (`Read`).

**Volatile.Read/Write** (~40 instances): Lightweight visibility for depth monitoring, idle TCS management, engine state diagnostics, dirty flags.

### Epoch Coordination

1. Each enqueued item calls `_epochTracker.Increment(epoch)` (uses `lock` because increment + peak tracking + epoch start time must be atomic together)
2. After processing, `item.TryMarkEpochComplete()` uses `Interlocked.Exchange` for single-fire
3. `_epochTracker.Decrement` returns true when count hits zero
4. If engine state is `AllIdle`, `HotPathIdle` event fires
5. Handler enqueues ALL epochs with pending work (not just the trigger epoch — FM-005 race condition mitigation)
6. `ProcessIdleEpochsAsync` reads from unbounded channel and runs idle processing

> `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs` — EpochTracker at line 1973

### Commit Batching

`IndexingCommitter` bridges concurrent pipeline output to single-writer DB:
- `_batchLock`: protects `_pendingItems` list for adding
- `_flushLock`: ensures only one flush at a time
- Timer fires every 100ms to flush accumulated items
- Threshold at 64 items triggers immediate flush
- `TaskCompletionSource` per item — callers await their item's completion

The double-lock pattern:
```csharp
lock (_flushLock)                    // Serialize flushes
{
    lock (_batchLock)                // Atomically drain pending items
    {
        batch = new List<PendingCommit>(_pendingItems);
        _pendingItems.Clear();
    }
    _db.IndexArtifactBatch(dbItems); // Single-threaded DB write
}
```

> `src/Indexing/RepoQL.Indexing/Indexing/Commit/IndexingCommitter.cs`

### Background Services

| Service | Base | Purpose |
|---------|------|---------|
| `RepoqlHost` | `BackgroundService` | Main indexing host lifecycle |
| `PipelineHealthPublisher` | `BackgroundService` | Periodic health status |
| `IdleShutdownHostedService` | `BackgroundService` | Auto-shutdown after idle period |
| `McpHostedService` | `IHostedService` | MCP server lifecycle |
| `CSharpWorkspaceHost` | `IHostedService` | Roslyn workspace |
| `IFileSystemWatcher` | `IHostedService` + `IAsyncDisposable` | File system watching |

---

## Async/Await

### The State Machine

The C# compiler transforms `async` methods into state machine structs implementing `IAsyncStateMachine`. Each `await` boundary becomes a state transition. When an `await` encounters an incomplete `Task`, the state machine saves locals and returns an incomplete `Task` to the caller. When the awaited operation completes, the continuation is scheduled via the captured context.

In release builds, the state machine is a `struct` to avoid allocation when the method completes synchronously. It gets boxed to the heap only if the method actually yields.

> [How Async/Await Really Works in C# — Stephen Toub, .NET Blog](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/)

### SynchronizationContext

Controls where continuations execute after an `await`:

| Environment | Context | Effect |
|-------------|---------|--------|
| WPF/WinForms | UI thread context | Continuations marshal back to UI thread |
| Classic ASP.NET | Request context | One-thread-at-a-time per request |
| ASP.NET Core | **None** | Continuations run on thread pool |
| Console / gRPC host | **None** | Continuations run on thread pool |

**For RepoQL**: There is no `SynchronizationContext`. `ConfigureAwait(false)` is technically a no-op but is used consistently (~105 call sites) as good practice for library code portability.

> [ASP.NET Core SynchronizationContext — Stephen Cleary](https://blog.stephencleary.com/2017/03/aspnetcore-synchronization-context.html)

### ConfigureAwait(false)

Key facts often misunderstood:

- Configures the **await**, not the task. `task.ConfigureAwait(false); await task;` on separate lines discards the configuration — does NOT work.
- Only takes effect if the await actually **yields**. If the task is already complete, execution continues synchronously regardless.
- Is **not** a deadlock prevention mechanism. Stephen Cleary: "ConfigureAwait(false) is not a good way to avoid deadlocks."
- Must be applied at **every** await point in a chain, not just the first or last.

> [ConfigureAwait in .NET 8 — Stephen Cleary](https://blog.stephencleary.com/2023/11/configureawait-in-net-8.html)

### ConfigureAwaitOptions (.NET 8)

| Value | Meaning |
|-------|---------|
| `None` | Equivalent to `ConfigureAwait(false)` |
| `ContinueOnCapturedContext` | Equivalent to `ConfigureAwait(true)` |
| `SuppressThrowing` | Suppresses exceptions during await (fire-and-observe) |
| `ForceYielding` | Forces async behavior even if task is complete (useful for testing/fairness) |

> [ConfigureAwaitOptions in .NET 8 — Bart Wullems](https://bartwullems.blogspot.com/2024/03/configureawaitoptions-in-net-8.html)

### Task vs ValueTask

**Default to `Task`.** Only use `ValueTask` when:
- The method completes synchronously 80-90%+ of the time
- Called very frequently (hundreds of thousands of times)
- Hot path with benchmarks proving allocation is a bottleneck

**Critical `ValueTask` restrictions:**
- Can only be awaited **once** (may be backed by pooled `IValueTaskSource` which gets recycled)
- Cannot be stored and awaited by multiple consumers
- Cannot be `.Result`'d after awaiting
- Use `.AsTask()` if you need Task semantics (`Task.WhenAll`, multiple awaits)

**Performance**: Zero-allocation on synchronous path. In .NET 5+, async ValueTask methods can opt into state machine pooling (`DOTNET_SYSTEM_THREADING_POOLASYNCVALUETASKS=true`), eliminating allocation even on the async path.

**RepoQL usage**: `ValueTask` used sparingly — `WorkQueue.EnqueueAsync` (avoiding allocation on dedupe skip), `DisposeAsync` implementations, `McpClientRegistry.DisposeAsync`. Most async methods return `Task`.

> [Understanding the Whys, Whats, and Whens of ValueTask — Stephen Toub, .NET Blog](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/)
> [Async ValueTask Pooling in .NET 5 — Stephen Toub](https://devblogs.microsoft.com/dotnet/async-valuetask-pooling-in-net-5/)

---

## System.Threading.Channels

### Bounded vs Unbounded

| Aspect | Bounded | Unbounded |
|--------|---------|-----------|
| Capacity | Fixed maximum | Unlimited |
| Backpressure | Built-in (writer waits when full) | None (memory grows unbounded) |
| Memory safety | Predictable | Risk of OOM under sustained producer advantage |
| Write performance | May need to wait | Synchronous writes always succeed |
| **Recommendation** | **Default choice** | Only when producer rate is guaranteed <= consumer rate |

### BoundedChannelFullMode

| Mode | Behavior | When to use |
|------|----------|-------------|
| `Wait` (default) | WriteAsync blocks until space available | Work that must not be lost |
| `DropNewest` | Drops newest item in channel | Status/event streams where freshness matters |
| `DropOldest` | Drops oldest item in channel | Status/event streams where latest matters most |
| `DropWrite` | Drops the item being written | When caller can detect and handle the drop |

### Performance Optimizations

- `SingleWriter = true` / `SingleReader = true` eliminate synchronization overhead when applicable
- `UnboundedChannel<T>` uses `ConcurrentQueue<T>` internally — lock-free
- `BoundedChannel<T>` uses a deque protected by a lock — optimized but not lock-free
- `AllowSynchronousContinuations = false` prevents callback capture (safer but slightly more overhead)

### Completion Propagation

The most important pattern for pipeline correctness:

```csharp
try
{
    await foreach (var item in reader.ReadAllAsync(ct))
    {
        var result = await ProcessAsync(item, ct);
        await output.WriteAsync(result, ct);
    }
}
catch (Exception ex)
{
    output.TryComplete(ex); // propagate error downstream
    throw;
}
finally
{
    output.TryComplete(); // signal no more items
}
```

Each stage must call `TryComplete()` on its output channel. Without this, downstream stages hang waiting for input that never comes.

> [An Introduction to System.Threading.Channels — Stephen Toub, .NET Blog](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/)
> [Channels — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
> [.NET Internals: System.Threading.Channels — Steve Gordon](https://www.stevejgordon.co.uk/dotnet-internals-system-threading-channels-unboundedchannel-part-2)

---

## Concurrent Collections

### ConcurrentDictionary

The most-used concurrent collection in RepoQL (40+ instances).

**The `GetOrAdd` atomicity trap**: The value factory is called **outside** the internal lock. Multiple threads calling `GetOrAdd` with the same key can each invoke the factory, but only one result is stored. The others are discarded. This means expensive initialization (DB connections, file handles) can be duplicated and thrown away.

**The `Lazy<T>` fix**:
```csharp
// Multiple Lazy<T> instances may be created (cheap), but only one is stored.
// Lazy<T>.Value ensures the expensive delegate runs exactly once.
var lazyValue = dict.GetOrAdd(key, k => new Lazy<TValue>(() => ExpensiveCreate(k)));
var value = lazyValue.Value;
```

**`AddOrUpdate` has the same issue** — both factories are called outside the lock and may be invoked multiple times.

**The check-then-act race**:
```csharp
// RACE CONDITION:
if (!dict.ContainsKey(key))
    dict.TryAdd(key, value); // Another thread may have added between check and add

// CORRECT:
dict.GetOrAdd(key, valueFactory);
```

> [Making ConcurrentDictionary GetOrAdd thread safe using Lazy — Andrew Lock](https://andrewlock.net/making-getoradd-on-concurrentdictionary-thread-safe-using-lazy/)
> [ConcurrentDictionary.GetOrAdd is not always thread safe — dotnet/runtime#33221](https://github.com/dotnet/runtime/issues/33221)

### ImmutableDictionary & ImmutableInterlocked

Thread-safe through immutability — every modification returns a new instance. The core update pattern:

```csharp
ImmutableInterlocked.Update(ref _dictionary, dict => dict.Add(key, value));
```

Under the hood: read current → apply transform → `Interlocked.CompareExchange` to swap → retry if another thread won the race. **The transformation function must be side-effect free** because it may run multiple times.

**Performance hierarchy** for reads: `FrozenDictionary` > `ReadOnlyDictionary` > `Dictionary` > `ConcurrentDictionary` > `ImmutableDictionary`

`ImmutableDictionary` is consistently the **worst reader** due to its balanced binary tree structure.

> [ImmutableInterlocked.Update — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.immutable.immutableinterlocked.update)
> [ImmutableInterlocked — Chris Cavanagh](https://chriscavanagh.wordpress.com/2014/08/21/immutableinterlocked/)

### FrozenDictionary (.NET 8)

47% faster reads than `Dictionary<K,V>` on average, up to 2.4x with certain key types. Uses less memory for larger collections.

**Trade-off**: High creation cost (~4300 microseconds). Need ~630,000 reads to break even. Ideal for configuration, lookup tables, data populated once and read millions of times.

> [FrozenDictionary performance — code-corner.dev](https://code-corner.dev/2023/11/08/NET-8-%E2%80%94-FrozenDictionary-performance/)
> [FrozenDictionary benchmarks — Dave Callan](https://davecallan.com/dotnet-8-frozendictionary-benchmarks/)

### When to Use Each

| Collection | Best For |
|------------|----------|
| `ConcurrentDictionary` | Frequent concurrent reads AND writes |
| `ImmutableDictionary` | Read-heavy with rare updates, snapshot semantics |
| `FrozenDictionary` | Write-once, read-millions lookup tables |
| `ConcurrentQueue` | Simple FIFO without backpressure |
| `Channel<T>` | Producer-consumer with backpressure and async |

---

## Locking Primitives

### Decision Matrix

| Primitive | Async-Compatible | Reader/Writer | Reentrant | Use Case |
|-----------|-----------------|---------------|-----------|----------|
| `lock` / `Lock` (.NET 9) | No | Exclusive | Yes (same thread) | Short synchronous critical sections |
| `SemaphoreSlim(1,1)` | **Yes** (`WaitAsync`) | Exclusive | **No** | Async mutual exclusion |
| `SemaphoreSlim(N,N)` | **Yes** | Limited concurrency | No | Throttling / resource pools |
| `ReaderWriterLockSlim` | No | Yes | Configurable | Read-heavy synchronous workloads |
| `Nito.AsyncEx.AsyncLock` | **Yes** | Exclusive | Yes | Async mutex with reentrancy |
| `Nito.AsyncEx.AsyncReaderWriterLock` | **Yes** | Yes | No | Async reader/writer |

### Why lock Cannot Be Used With await

The compiler prevents `await` inside a `lock` because `Monitor.Enter`/`Monitor.Exit` must happen on the same thread. An `await` may resume on a different thread, causing `SynchronizationLockException`.

### SemaphoreSlim as Async Mutex

```csharp
await _semaphore.WaitAsync(cancellationToken);
try
{
    // async critical section — can use await here
}
finally
{
    _semaphore.Release();
}
```

**Always** pass `CancellationToken` to `WaitAsync`. **Always** use `try/finally` for release. **Not reentrant** — the same logical async flow acquiring twice will deadlock.

> [Efficient Synchronization in C# with SemaphoreSlim — Oleg Kyrylchuk](https://okyrylchuk.dev/blog/efficient-synchronization-in-csharp-with-semaphoreslim/)

### System.Threading.Lock (.NET 9 / C# 13)

Dedicated lock type replacing `lock(object)`. More efficient than Monitor-based locking, clearer intent, compiler warnings on misuse. **Still synchronous-only.**

> [Enhancing Thread Safety with the New Lock — Apurv Upadhyay](https://apurvupadhyay.medium.com/enhancing-thread-safety-with-the-new-79306d56b896)

### Common Deadlock Patterns

| Pattern | Mechanism | Fix |
|---------|-----------|-----|
| Lock ordering | Two threads acquire locks A,B in opposite order | Always acquire in consistent global order |
| Sync-over-async | `.Result`/`.Wait()` blocks the context thread that the continuation needs | Use `await` end-to-end |
| Nested lock + async | `lock` is thread-affine but continuation may resume on different thread | Use `SemaphoreSlim` |
| Reentrant SemaphoreSlim | Same logical flow acquires twice | Design to avoid reentrancy |

> [Deadlock Prevention — LearnCSharpMastery](https://learncsharpmastery.com/deadlock-prevention/)

---

## Interlocked & Memory Model

### Interlocked Methods

| Method | Purpose | Cost |
|--------|---------|------|
| `Increment(ref int)` | Atomic counter increment | ~6ns |
| `Decrement(ref int)` | Atomic counter decrement | ~6ns |
| `Exchange(ref T, T)` | Atomic swap, returns old value | ~6ns |
| `CompareExchange(ref T, T, T)` | CAS: swap only if current matches expected | ~6ns |
| `Read(ref long)` | Atomic 64-bit read (needed on 32-bit targets) | ~6ns |

For comparison, `lock` costs ~40ns. `Interlocked` is the foundation of lock-free programming but only protects single variables — multi-step operations still need locks.

**RepoQL patterns**: `Increment`/`Decrement` for counters (WorkQueue, epochs), `Exchange` for one-shot flags (epoch completion), `CompareExchange` for state transitions (VSS worker startup).

> [Interlocked Class — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked)

### Volatile

`Volatile.Read` provides acquire semantics (ensures subsequent reads see values at least as fresh). `Volatile.Write` provides release semantics (ensures previous writes are visible before this one). On x86, volatile is essentially a no-op at the machine code level because x86 provides strong ordering natively.

**The danger**: Code that works on x86 may have subtle bugs on ARM because ARM's weaker memory model allows reordering that x86 masks. This matters with ARM64 Windows and Apple Silicon.

**Practical guidance**: Prefer `Interlocked` operations or proper locks over `volatile`. Jon Skeet: volatile's semantics are confusing and insufficient for most real synchronization needs.

> [The C# Memory Model in Theory and Practice — MSDN Magazine](https://learn.microsoft.com/en-us/archive/msdn-magazine/2012/december/csharp-the-csharp-memory-model-in-theory-and-practice)
> [Jon Skeet — Volatility, Atomicity and Interlocking](https://jonskeet.uk/csharp/threads/volatility.html)
> [dotnet/runtime Memory Model Spec](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Memory-model.md)

### Three Specifications That Disagree

| Spec | Strength | Notes |
|------|----------|-------|
| ECMA-335 (CLI) | Very weak | Allows aggressive reordering |
| C# language spec | Stronger | Adds acquire/release for volatile |
| Actual CLR runtime | **Strongest** | Practical compromise; documented in runtime repo |

The runtime is stronger than either spec. Code relying on runtime behavior (not spec) is technically non-portable but works in practice on all current .NET implementations.

> [dotnet/runtime Memory-model.md](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Memory-model.md)
> [Joe Duffy — Volatile reads and writes, and timeliness](https://joeduffyblog.com/2008/06/13/volatile-reads-and-writes-and-timeliness/)

---

## Cancellation

### Core Architecture

- `CancellationTokenSource` (CTS): creates and controls the token ("signal sender")
- `CancellationToken` (CT): lightweight struct passed to operations ("signal receiver")
- Model is **cooperative**: cancellation is requested, not forced

### Linked Tokens

`CreateLinkedTokenSource` creates a CTS that cancels when **any** parent cancels:

```csharp
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
linkedCts.CancelAfter(TimeSpan.FromSeconds(30));
await DoWorkAsync(linkedCts.Token);
```

**Always dispose linked CTS.** Linked sources register callbacks on parent tokens. Undisposed registrations leak memory.

> [Cancellation, Part 6: Linking — Stephen Cleary](https://blog.stephencleary.com/2024/10/cancellation-6-linking.html)
> [Memory leak from linked cancellation — dotnet/runtime#78180](https://github.com/dotnet/runtime/issues/78180)

### Determining Which Token Canceled

```csharp
catch (OperationCanceledException)
{
    if (timeoutCts.IsCancellationRequested) { /* timeout */ }
    else if (externalToken.IsCancellationRequested) { /* external */ }
}
```

### RepoQL Patterns

- **WorkQueue timeout**: `CancellationTokenSource.CreateLinkedTokenSource` + `Task.WaitAsync(_itemTimeout)` for per-item timeouts
- **IndexingEngine shutdown**: Central `Shutdown` CTS with `Register(() => channel.Writer.TryComplete())` for channel cleanup
- **DuckDB command cancellation**: `cancellationToken.Register(cmd.Cancel)` bridges cooperative cancellation to DuckDB's command API
- **VectorIndexCoordinator**: Separate `_vssRefreshShutdown` CTS for independent shutdown control

### Best Practices

1. **Flow tokens through every async method** — all the way down
2. **Check `token.ThrowIfCancellationRequested()`** at the top of CPU-bound loops
3. **Pass tokens to I/O operations** (`ReadAsync(buffer, token)`, `WaitAsync(token)`)
4. **Don't wrap unnecessarily** — if just passing through, don't create a new CTS
5. **Handle at pipeline boundaries** — catch, log, propagate channel completion downstream
6. **After point of no return** (side effects committed), pass `CancellationToken.None` downstream
7. **Use `WaitAsync(token)`** to add cancellation to APIs that don't accept a token

> [Mastering Cancellation in C# — Oleg Kyrylchuk](https://okyrylchuk.dev/blog/mastering-cancellation-in-csharp-with-cancellationtoken/)
> [Cancellation Tokens Best Practices — NileBits](https://www.nilebits.com/blog/2024/06/cancellation-tokens-in-csharp/)

---

## Coordination Primitives

### TaskCompletionSource

The bridge between callback/event-based code and `Task`-based code. Represents a manually-controlled `Task`:

| Method | Effect |
|--------|--------|
| `SetResult(T)` / `TrySetResult(T)` | Completes successfully |
| `SetException(Exception)` | Faults the task |
| `SetCanceled()` | Cancels the task |

**Always use `TaskCreationOptions.RunContinuationsAsynchronously`** to prevent continuations from running inline on the thread that calls `SetResult`. Without this, calling `SetResult` can unexpectedly run arbitrary continuation code on your thread.

**RepoQL uses this consistently** (~35 `TaskCompletionSource` instances, all with `RunContinuationsAsynchronously`): WorkQueue idle detection, IndexingCommitter per-item completion, operation completion signals.

> [Building Async Coordination Primitives — Stephen Toub, .NET Blog](https://devblogs.microsoft.com/dotnet/building-async-coordination-primitives-part-1-asyncmanualresetevent/)

### Other Primitives

| Primitive | Async | Use When |
|-----------|-------|----------|
| `ManualResetEventSlim` | No | Synchronous thread signaling (up to 50x faster than `ManualResetEvent` in short-wait scenarios) |
| `CountdownEvent` | No | Waiting for N operations to complete |
| `Barrier` | No | Multi-phase parallel algorithms where all workers must sync at phase boundaries |

For async coordination, prefer `TaskCompletionSource` or `Channel<T>` — they integrate naturally with `await`.

> [Overview of synchronization primitives — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/threading/overview-of-synchronization-primitives)

---

## The Single-Writer Principle

### The Concept

Any data should be owned by only one thread for write access. This eliminates write contention entirely. Reads can be concurrent and lock-free if they read consistent snapshots.

> [Mechanical Sympathy: Single Writer Principle — Martin Thompson](https://mechanical-sympathy.blogspot.com/2011/09/single-writer-principle.html)

### .NET Implementation via Channels

The most practical .NET implementation uses `Channel<T>` as an inbox:

```
Multiple Producers → Channel (inbox) → Single Consumer (owns all mutable state)
```

The consumer processes commands sequentially. Multiple producers send messages via `WriteAsync`. No locks needed because only one thread writes. This is a lightweight actor.

DuckDB's single-writer constraint is literally this pattern. The `IndexingCommitter` batches concurrent pipeline output into single-threaded DB writes.

### LMAX Disruptor

The LMAX system processes 6 million orders per second on a single thread via a ring buffer where each producer/consumer has its own sequence counter. Available for .NET: [disruptor-net](https://github.com/disruptor-net/Disruptor-net). Relevant as a reference architecture but likely over-engineered for RepoQL's throughput requirements.

> [The LMAX Architecture — Martin Fowler](https://martinfowler.com/articles/lmax.html)

---

## AsyncLocal and ExecutionContext

### How Context Flows

`AsyncLocal<T>` stores values in `ExecutionContext`, which is captured and restored across `await` points:

- **Copy-on-write**: Setting a new value creates a new copy for that branch. The parent's context is unaffected.
- **Top-down flow**: Values flow from parent to child, not the reverse.
- **ConfigureAwait(false) does NOT suppress ExecutionContext**: Only suppresses `SynchronizationContext`. `ExecutionContext` (and thus `AsyncLocal`) always flows unless explicitly suppressed with `ExecutionContext.SuppressFlow()`.

**RepoQL usage**: `AsyncLocal<IServiceScope?>` in `DuckDbDataStore` flows DI scope to UDF callbacks executing inside DuckDB queries.

### Mutable Objects Are Dangerous

Storing a mutable object (like `List<T>`) in `AsyncLocal<T>` — the copy-on-write is on the *reference*, not the object. All branches see the same mutable object. **Not thread-safe.** Store immutable types or use `ImmutableInterlocked` patterns.

### Where Context Does NOT Flow

- `ExecutionContext.SuppressFlow()` suppresses flow
- `ThreadPool.UnsafeQueueUserWorkItem` skips flow
- `AsyncLocal` does not survive `yield return` statements

> [Implicit Async Context ("AsyncLocal") — Stephen Cleary](https://blog.stephencleary.com/2013/04/implicit-async-context-asynclocal.html)
> [Persist Values With AsyncLocal — Code Maze](https://code-maze.com/csharp-persist-values-with-asynclocal-in-async-flow/)
> [AsyncLocal does not survive yield return — dotnet/runtime#47802](https://github.com/dotnet/runtime/issues/47802)

---

## Background Services

### Key Behavioral Change (.NET 6)

If `ExecuteAsync` throws an unhandled exception, the **entire host stops** (default: `BackgroundServiceExceptionBehavior.StopHost`). Pre-.NET 6, exceptions were silently swallowed.

**Best practice**: Wrap `ExecuteAsync` in try/catch, handle explicitly, call `IHostApplicationLifetime.StopApplication()` for fatal errors.

> [.NET 6 breaking change: Exception handling — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/hosting-exception-handling)
> [BackgroundService Gotcha: Silent Failures — Stephen Cleary](https://blog.stephencleary.com/2020/05/backgroundservice-gotcha-silent-failure.html)

### Graceful Shutdown

- `StopAsync` gets a default **30-second timeout** via `CancellationToken`
- Configure via `HostOptions.ShutdownTimeout` for heavy workloads
- `OperationCanceledException` during shutdown is **expected** — log at Information, not Error

### Concurrent Startup/Shutdown (.NET 8)

```csharp
services.Configure<HostOptions>(options =>
{
    options.ServicesStartConcurrently = true;
    options.ServicesStopConcurrently = true;
});
```

Application ready as fast as the slowest service, not the sum. With concurrent startup, explicit coordination between dependent services becomes more important.

> [Concurrent Hosted Service Start and Stop — Steve Gordon](https://www.stevejgordon.co.uk/concurrent-hosted-service-start-and-stop-in-dotnet-8)

---

## Thread Pool

### Architecture

Two pools: **worker threads** (execute `Task.Run`, etc.) and **I/O completion port threads** (handle async I/O callbacks). A hill-climbing algorithm automatically adjusts thread count.

### The Growth Problem

After reaching minimum (typically `Environment.ProcessorCount`), the pool injects threads at **~1 per 500ms**. Consequences:

| Blocked threads | Recovery time |
|----------------|---------------|
| 8 (on 8-core) | ~4 seconds |
| 100 | ~50 seconds |
| 1000 | ~500 seconds |

This is why sync-over-async is devastating. The starvation cascade: `.Result`/`.Wait()` blocks a thread → pool slowly grows → more requests arrive and block → entire application grinds to a halt.

> [Debug ThreadPool Starvation — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation)
> [.NET ThreadPool starvation — Criteo Engineering](https://medium.com/criteo-engineering/net-threadpool-starvation-and-how-queuing-makes-it-worse-512c8d570527)

### SetMinThreads

```csharp
ThreadPool.SetMinThreads(workerThreads: 100, completionPortThreads: 100);
```

A **temporary mitigation** when blocking calls can't be removed immediately. Excessively high values waste memory (~1MB stack per thread) and hurt CPU-bound workloads through context switching. The long-term fix is always eliminating blocking calls.

### For Developer Laptops

- Don't set `SetMinThreads` to extreme values
- Use bounded parallelism (`ProcessorCount / 2` for CPU-bound work)
- Prefer async I/O everywhere
- Monitor with `ThreadPool.ThreadCount` and `ThreadPool.PendingWorkItemCount`

### .NET Runtime Improvements

| Version | Improvement |
|---------|-------------|
| .NET 6 | Thread pool rewritten, faster scaling for sync-over-async patterns |
| .NET 9 | `WaitHandleWait` diagnostic event when a thread blocks; smarter scheduling |
| .NET 10 | **Local-to-global queue flushing**: when a thread blocks, all items from its local queue are automatically moved to the global queue, preventing trapped work items and the classic sync-over-async deadlock under load |

The .NET 10 thread pool change is significant. Previously, a blocked thread's local work-stealing queue retained its items — other threads couldn't see them until they happened to steal from that queue. Under load with sync-over-async code, this caused cascading stalls. .NET 10 flushes local queues to global on block, making all pending work immediately discoverable.

> [Threading config settings — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading)
> [What's new in .NET 9 runtime — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9/runtime)
> [Why .NET 10 Threading Feels Faster — Medium](https://medium.com/c-sharp-programming/dotnet10-threadpool-fixes-9e9491059846)
> [What's new in .NET 10 runtime — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)

---

## Timer Patterns

### PeriodicTimer (.NET 6+) vs System.Threading.Timer

| Feature | System.Threading.Timer | PeriodicTimer |
|---------|----------------------|---------------|
| Mechanism | Callback on ThreadPool thread | Awaitable `WaitForNextTickAsync()` |
| Overlapping execution | Yes — if callback outlasts interval | **No** — next tick waits for previous |
| Error handling | Must guard manually | Flows through async/await |
| Reentrancy | Must guard with locks | Impossible by design |
| Cancellation | Manual | Built-in `CancellationToken` |
| Memory leaks | Common (GC can collect timer) | Less susceptible |

**PeriodicTimer is the clear choice** for new code. The elimination of overlapping execution makes it fundamentally safer.

**RepoQL note**: The `IndexingCommitter` uses `System.Threading.Timer` for 100ms flush intervals. This predates `PeriodicTimer` availability but the callback is protected by `_flushLock`.

### Debouncing

RepoQL implements `KeyedDebouncer` using `ConcurrentDictionary` + `CancellationTokenSource` + `Task.Delay`. Notable race condition mitigation: CTS disposal is delayed 10ms via `Task.Run` to avoid disposing while a pending operation might still reference it.

> `src/RepoQL.Core/KeyedDebouncer.cs`

> [PeriodicTimer in C# — Code Maze](https://code-maze.com/csharp-periodic-timer/)
> [.NET Timers: All You Need to Know — Vasil Kosturski](https://medium.com/@vosarat1995/net-timers-all-you-need-to-know-d020c73b63a4)

---

## gRPC and Concurrency

### Service Lifetime

ASP.NET Core gRPC services are **transient by default** — each call gets a fresh instance. Scoped DI services work naturally.

### Streaming Thread Safety

**Critical**: `IAsyncStreamReader<T>` and `IServerStreamWriter<T>` can each be used by only one thread at a time. Cannot read or write on multiple threads simultaneously. **Can** use reader and writer on separate threads from each other.

**Pattern**: Use `Channel<T>` as a bridge. Multiple threads write to a channel; a single loop reads from the channel and writes to the gRPC stream.

### Cancellation

- `ServerCallContext.CancellationToken` fires when client disconnects or cancels
- Background tasks must complete before the method returns — using context/reader/writer after method exit causes errors
- Known deadlock risk with ASP.NET ConcurrencyLimiter middleware and client-streaming

> [Performance best practices with gRPC — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance)
> [gRPC services with ASP.NET Core — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore)

---

## Async Disposal

### IAsyncDisposable Pattern

```csharp
public async ValueTask DisposeAsync()
{
    await DisposeAsyncCore().ConfigureAwait(false);
    Dispose(disposing: false);
    GC.SuppressFinalize(this);
}

protected virtual async ValueTask DisposeAsyncCore()
{
    _outputChannel.Writer.TryComplete();
    await _backgroundTask.ConfigureAwait(false);
}
```

Returns `ValueTask`, not `Task`. Implement **both** `IDisposable` and `IAsyncDisposable`.

**RepoQL implements this on**: `WorkQueue<T>`, `IndexingEngine`, `McpClientRegistry`, `FileSystemWatcherBase`, `IRepoQlClient`, `TypeScriptNodeClient`. WorkQueue uses a 2-second grace period:

```csharp
var completed = await Task.WhenAny(allReaders, Task.Delay(TimeSpan.FromSeconds(2)));
```

> [Implement a DisposeAsync method — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync)

### SemaphoreSlim Disposal Dangers

`SemaphoreSlim.Dispose()` does **not** throw `ObjectDisposedException` on already-waiting `WaitAsync` callers — waiting tasks just hang forever. If `WaitAsync` is called *after* `Dispose()`, it throws. This asymmetry is a known design issue.

**Safe shutdown pattern**:
1. Cancel all waiters via `CancellationToken` (let them exit cleanly)
2. Wait for all consumers to finish
3. Only then call `Dispose()`

`SemaphoreSlim` does NOT implement `IAsyncDisposable` — only synchronous `Dispose()`.

> [SemaphoreSlim Dispose thread safety — dotnet/runtime#15047](https://github.com/dotnet/runtime/issues/15047)
> [SemaphoreSlim.WaitAsync not cancelled on Dispose — dotnet/runtime#59639](https://github.com/dotnet/runtime/issues/59639)

---

## Structured Concurrency

### .NET's Current State

.NET does **not** have built-in structured concurrency. `Task.WhenAll` has gaps:
- If one task throws, other tasks continue running (not cancelled)
- `await Task.WhenAll(...)` only throws the *first* exception; others are buried in `AggregateException` on `.Exception`
- No scope mechanism to prevent orphaned tasks

### Stephen Cleary's TaskGroup

[StephenCleary/StructuredConcurrency](https://github.com/StephenCleary/StructuredConcurrency) addresses this:
- `TaskGroup.RunGroupAsync` creates a scope
- If any work throws (except `OperationCanceledException`), the group cancels all other work
- More work can be added dynamically; the group extends its logical WhenAll

Not widely adopted. No official .NET runtime proposal found.

### Custom TaskScheduler: Don't

Custom `TaskScheduler` is fundamentally incompatible with `async`/`await`. The scheduler only applies to the first segment (before the first `await`). After that, continuations may or may not use the same scheduler. Use `Channel<T>`, `SemaphoreSlim`, or `Parallel.ForEachAsync` instead.

> [You probably should stop using a custom TaskScheduler — Sergey Teplyakov](https://sergeyteplyakov.github.io/Blog/csharp/2024/06/14/Custom_Task_Scheduler.html)

---

## Parallel.ForEachAsync (.NET 6+)

```csharp
await Parallel.ForEachAsync(files, new ParallelOptions
{
    MaxDegreeOfParallelism = Environment.ProcessorCount,
    CancellationToken = cancellationToken
}, async (file, ct) =>
{
    await ProcessFileAsync(file, ct);
});
```

- Default `MaxDegreeOfParallelism` is `Environment.ProcessorCount`
- If any iteration throws, the loop stops starting new iterations, waits for in-flight iterations, throws first exception
- Respects `CancellationToken`

**When to use vs alternatives**:

| Approach | Best For |
|----------|----------|
| `Parallel.ForEachAsync` | Processing a finite collection with bounded parallelism |
| `Channel`-based pipeline | Continuous streaming, multi-stage, backpressure |
| `Task.WhenAll` + `SemaphoreSlim` | Fine-grained throttling and error handling |

**RepoQL note**: No `Parallel.ForEachAsync` in core code. One instance in `OpenRouterEmbeddingProvider.cs`. The pipeline uses `WorkQueue<T>` with configurable worker counts instead.

> [Parallel.ForEachAsync in .NET 6 — Scott Hanselman](https://www.hanselman.com/blog/parallelforeachasync-in-net-6)
> [Parallel.ForEachAsync and Exceptions — Jeremy Bytes](https://jeremybytes.blogspot.com/2024/02/parallelforeachasync-and-exceptions.html)

---

## .NET 10 Concurrency-Relevant Changes

.NET 10 (LTS, released November 2025) introduces no new concurrency primitives but has meaningful runtime improvements:

### Thread Pool: Local-to-Global Queue Flushing

Covered above in Thread Pool section. The most impactful concurrency change — eliminates an entire class of sync-over-async deadlocks.

### Stack Allocation of Small Arrays

The JIT now stack-allocates small, fixed-sized arrays of both value types AND reference types when escape analysis proves they don't outlive the method. Additionally, escape analysis now covers local struct fields and delegates. This reduces GC pressure during concurrent work — fewer allocations means fewer GC pauses interrupting pipeline throughput.

> [What's new in .NET 10 runtime — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)

### Array Interface Devirtualization

`foreach` over `IEnumerable<T>` backed by arrays gets devirtualized and inlined. The enumerator can be stack-allocated. Relevant for pipeline hot paths that iterate collections polymorphically.

### ARM64 Write Barrier Improvements

Dynamic write-barrier switching (previously x64-only) now available on ARM64. GC pause improvements of 8-20% on ARM64 hardware. Relevant for developer laptops with ARM processors (Windows on ARM, Apple Silicon via Rosetta considerations).

### JIT Inlining and De-abstraction

Cascading devirtualization (previous inlining enables further devirtualization), inlining of `try-finally` methods, and profile-guided size tolerance increases. These compound to reduce overhead in abstraction-heavy code like pipeline stages and format handlers.

> [.NET 10 de-abstraction plans — dotnet/runtime#108913](https://github.com/dotnet/runtime/issues/108913)

---

## Diagnostics and Metrics

### What to Monitor

| Metric | Source | Indicates |
|--------|--------|-----------|
| Queue length growing + thread count at minimum | `System.Runtime` | Thread pool starvation |
| Lock contention count rising | `Monitor.LockContentionCount` | Contention hot spots |
| Timer count rising without plateau | `System.Threading.Timer.ActiveCount` | Timer leak |
| Completed work items dropping | `threadpool-completed-work-item-count` | Throughput degradation |

### Tools

- `dotnet-counters monitor System.Runtime` for real-time monitoring
- OpenTelemetry integration via `System.Diagnostics.Metrics` (modern, recommended)
- .NET 9: `WaitHandleWait` diagnostic event emitted when a thread blocks
- `Monitor.LockContentionCount` counts total contentions but doesn't identify which lock — need dotnet-trace or PerfView for that

> [.NET runtime metrics — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)
> [Well-known EventCounters in .NET — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters)

---

## Common Pitfalls Summary

| Pitfall | Consequence | Prevention |
|---------|-------------|------------|
| `async void` | Exceptions crash the process | Always return `Task`/`ValueTask` |
| `.Result` / `.Wait()` | Thread pool starvation | `await` end-to-end |
| Fire-and-forget `_ = DoWorkAsync()` | Silent exception swallowing | Wrap in `Task.Run` with try/catch, or use channels |
| `GetOrAdd` with expensive factory | Duplicate initialization, wasted resources | Use `Lazy<T>` pattern |
| `lock` + `await` | `SynchronizationLockException` | Use `SemaphoreSlim(1,1)` |
| Undisposed linked CTS | Memory leak | Always `using` linked CTS |
| SemaphoreSlim dispose while waited | Waiters hang forever | Cancel first, wait for exit, then dispose |
| Missing channel `TryComplete()` | Downstream hangs forever | Always complete in `finally` |
| Mutable objects in `AsyncLocal` | Race conditions | Store immutable types |
| `ConfigureAwait(false)` on separate line | No effect (return value discarded) | `await task.ConfigureAwait(false)` as one expression |
| TCS without `RunContinuationsAsynchronously` | Arbitrary code runs on signaling thread | Always pass the option |
| `Parallel.ForEachAsync` swallows late exceptions | Only first exception propagates | Design for first-failure-stops semantics |
| Custom `TaskScheduler` | Incompatible with async/await after first yield | Use `Channel<T>` or `SemaphoreSlim` |

---

## Codebase Observations

### Consistent Patterns

The codebase is highly consistent:
- `Channel<T>` for producer-consumer (never `BlockingCollection`)
- `ConcurrentDictionary` for thread-safe state (40+ instances)
- `SemaphoreSlim(1,1)` for async mutexes
- `lock` for simple synchronous sections
- `Interlocked` for counters and flags
- `Volatile` for lightweight visibility
- `TaskCreationOptions.RunContinuationsAsynchronously` everywhere
- `ConfigureAwait(false)` throughout library code (~105 sites)

### Notable Choices

| Choice | Detail |
|--------|--------|
| No `ReaderWriterLockSlim` | Despite README mentioning it, actual implementation uses custom `Monitor.Wait/PulseAll` gate |
| No `BlockingCollection` | `Channel<T>` used exclusively (modern replacement) |
| No `Parallel.ForEachAsync` in core | Pipeline uses `WorkQueue<T>` with configurable worker counts |
| Two `WorkQueue<T>` copies | `RepoQL.Indexing.WorkQueue<T>` (richer) and `RepoQL.Core.WorkQueue<T>` (simpler original) |
| Synchronous DB writes | `DuckDbDataStore.WriteTransaction` methods are synchronous; `IndexingCommitter` bridges via `TaskCompletionSource` |

---

## Gaps

- **DuckDB async writes**: DuckDB's .NET client only supports synchronous writes. The `IndexingCommitter` bridges this with TCS, but the synchronous call blocks a thread pool thread during flush. Impact depends on flush frequency and duration.
- **No async reader-writer lock**: The `DuckDbDataStore` gate uses `Monitor.Wait/PulseAll` — thread-affine and not async-compatible. This works because the exclusive section is short, but it means waiting threads are blocked, not yielding.
- **PeriodicTimer migration**: `IndexingCommitter` and some other components use `System.Threading.Timer`. `PeriodicTimer` is safer but hasn't been adopted.
- **Structured concurrency**: No mechanism prevents orphaned tasks. If a pipeline stage crashes without completing its output channel, downstream stages hang until overall cancellation.
- **Thread pool metrics**: RepoQL uses OpenTelemetry but it's unclear whether thread pool starvation metrics are actively monitored.
- **ARM memory model**: Code relies on x86's strong ordering in some `Volatile` usage sites. Behavior on ARM64 (Windows on ARM, macOS) should be verified.
- **ReaderWriterLockSlim README mismatch**: `src/RepoQL.Data.DuckDB/README.md` states the data store uses `ReaderWriterLockSlim` but the actual implementation does not.
- **Channel<T> vs TPL Dataflow benchmarks**: No rigorous comparative data found. Channels are simpler and generally preferred; Dataflow offers branching/joining/batching built-in.
- **Lock contention identification**: Built-in metrics count total contentions but don't identify which lock. Requires profiling tools for diagnosis.
- **Nito.AsyncEx / DotNext evaluation**: Third-party async coordination libraries not evaluated for RepoQL fit. May be relevant for the DuckDbDataStore gate pattern.

---

## Third-Party Libraries Worth Knowing

| Library | What It Provides | Relevance |
|---------|-----------------|-----------|
| [Nito.AsyncEx](https://github.com/StephenCleary/AsyncEx) | `AsyncLock`, `AsyncReaderWriterLock`, `AsyncAutoResetEvent` | Async coordination beyond BCL |
| [DotNext](https://dotnet.github.io/dotNext/features/threading/index.html) | `AsyncExclusiveLock`, `AsyncReaderWriterLock`, `AsyncSharedLock` | FIFO wait queues, async barriers |
| [Disruptor-net](https://github.com/disruptor-net/Disruptor-net) | Ring buffer, sequence barriers | Ultra-low-latency single-writer |
| [StephenCleary/StructuredConcurrency](https://github.com/StephenCleary/StructuredConcurrency) | `TaskGroup` with error propagation and cancellation | Structured concurrency for .NET |
| [Microsoft.Extensions.ObjectPool](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool) | Object pooling | Pipeline stage instance reuse |
| [System.IO.Pipelines](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines) | High-performance byte-level I/O pipeline | Network/file I/O (separate concern) |

---

*Research conducted via codebase analysis (93+ files with CancellationToken, 40+ ConcurrentDictionary instances, 16 SemaphoreSlim instances, ~80 Interlocked call sites, ~40 Volatile sites, ~105 ConfigureAwait sites) and web sources (Microsoft Learn, .NET Blog, Stephen Cleary, Stephen Toub, Martin Thompson, Joe Duffy, Jon Skeet, dotnet/runtime issues). Source bias: Microsoft documentation and first-party .NET team blogs dominate — these are authoritative for .NET runtime behavior but may understate third-party alternatives.*
