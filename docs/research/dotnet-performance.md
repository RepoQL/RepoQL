---
description: ".NET performance patterns for long-running processes with native interop — memory management, GC configuration, profiling, and optimization for DuckDB, ONNX Runtime, Roslyn, and gRPC on shared developer machines."
tags: [performance, memory, gc, profiling, duckdb, onnx, roslyn, grpc, diagnostics]
audience: { human: 40, agent: 60 }
purpose: { research: 85, reference: 15 }
---

# .NET Performance: Long-Running Processes with Native Interop

Reference material for building performant .NET applications that combine managed code with native libraries (DuckDB, ONNX Runtime, Roslyn) and run on developer laptops alongside other memory-hungry processes.

*Research date: February 2026. Targets .NET 9/10.*

## Context

A long-running .NET host process that embeds DuckDB (native buffer manager), ONNX Runtime (native arena allocator), Roslyn (managed but heavy), and gRPC (IPC) presents a specific performance profile: three independent memory pools that the GC can't see as a whole, bursty workloads with idle periods, and a shared machine where the process can't claim all resources.

**What's in scope:** GC modes and configuration, native interop memory patterns, profiling tools, gRPC performance, concurrency primitives, memory-efficient coding patterns.

**What's out of scope:** Algorithm optimization, query planner tuning, application-specific throughput benchmarks.

---

## Memory Architecture: Three Independent Pools

Applications that embed native libraries have multiple memory pools invisible to each other.

### Pool 1: .NET Managed Heap (GC-tracked)

The garbage collector manages this pool. All C# object allocations live here. Visible via `GC.GetTotalMemory()`, `dotnet-counters`, `dotnet-gcdump`.

Typical heavy residents: `ConcurrentDictionary` instances without eviction, syntax trees and compilations, OpenTelemetry activities, string data from parsed files.

> [Microsoft Learn — Workstation vs. server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc) — GC mode behavioral differences

### Pool 2: DuckDB Native Memory (buffer manager)

Allocated by `duckdb.dll`. The .NET GC has zero visibility. Controlled by DuckDB's `memory_limit` setting, but non-buffer allocations (vectors, query results) can exceed this limit. Visible via `SELECT * FROM duckdb_memory()`.

> [DuckDB — Memory Management](https://duckdb.org/2024/07/09/memory-management) — buffer pool architecture, spill-to-disk behavior
> [DuckDB — Configuration](https://duckdb.org/docs/stable/configuration/overview) — memory_limit only applies to buffer manager

### Pool 3: ONNX Runtime Native Memory (arena allocator)

Allocated by `onnxruntime.dll`. The arena allocator pre-allocates memory and **never returns it to the system** during a session's lifetime. Session disposal should free arena memory, but known issues report incomplete release.

> [ONNX Runtime — Memory](https://onnxruntime.ai/docs/performance/tune-performance/memory.html) — arena behavior
> [ONNX Runtime Issue #14466](https://github.com/microsoft/onnxruntime/issues/14466) — memory not released after Dispose()
> [ONNX Runtime Issue #11627](https://github.com/microsoft/onnxruntime/issues/11627) — arena memory never returned to system

### The Visibility Problem

| Metric | Shows | Misses |
|--------|-------|--------|
| `GC.GetTotalMemory()` | Managed heap | All native allocations |
| `Process.WorkingSet64` | Physical pages in RAM | No breakdown by source |
| `Process.PrivateMemorySize64` | Total private committed | No breakdown by source |
| `dotnet-counters gc-heap-size` | GC heap over time | Native memory |
| `duckdb_memory()` SQL function | DuckDB buffer manager by category | Non-buffer DuckDB allocations |

A process can report 500 MB managed heap while consuming 4 GB total. The delta is native memory. `GC.AddMemoryPressure` informs the GC about native allocations so it schedules collections more aggressively — DuckDB.NET does not use it (confirmed by source inspection of [Giorgi/DuckDB.NET](https://github.com/Giorgi/DuckDB.NET)).

> [Microsoft Learn — GC.AddMemoryPressure](https://learn.microsoft.com/en-us/dotnet/api/system.gc.addmemorypressure) — tells GC about native allocations
> [dotnet/runtime Discussion #93717](https://github.com/dotnet/runtime/discussions/93717) — guidance on pairing with NativeMemory

---

## GC Modes and Configuration

### Server vs. Workstation GC

| Characteristic | Workstation | Server |
|----------------|-------------|--------|
| GC threads | User thread, normal priority | Dedicated threads per core, highest priority |
| Heaps | 1 | 1 per logical CPU |
| Memory footprint | Lower (~30-36 MB baseline) | Higher (~390 MB baseline in benchmarks) |
| Collection speed | Slower on same heap size | Faster due to parallel collection |
| Memory aggressiveness | Collects more frequently | Grows aggressively, shrinks reluctantly |
| Best for | Client apps, shared machines | Dedicated server processes |

Server GC on a shared developer laptop is problematic. The 90% high-memory threshold means GC only becomes aggressive about compaction when physical memory is nearly exhausted. On a 32 GB machine, a Server GC process can comfortably consume many GB before triggering compacting collections.

> [Microsoft Learn — Workstation vs. server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc) — behavioral comparison
> [dotnet/runtime #107345](https://github.com/dotnet/runtime/issues/107345) — high memory consumption with Server GC

### DATAS (Dynamic Adaptation to Application Sizes)

A hybrid between Workstation and Server GC. Starts with 1 heap, grows to match core count as needed, shrinks back. Heap size is proportional to long-lived data, not machine memory. Opt-in in .NET 8 (`"System.GC.DynamicAdaptationMode": 1`), enabled by default in .NET 9+.

| Dimension | Without DATAS | With DATAS |
|-----------|---------------|------------|
| Heap count | Fixed (1 or N) | Dynamic (1 to N) |
| Heap sizing | Based on available memory | Based on long-lived data |
| Working set | Can grow to fill available RAM | Constrained, ~80% reduction in benchmarks |
| Throughput | Optimized | 2-3% reduction |
| GC frequency | Fewer, longer | More frequent, shorter |
| LOH compaction | Manual/never | Automatic when fragmented |

> [Microsoft Learn — DATAS](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas) — design and behavior
> [Maoni Stephens — Dynamically Adapting to Application Sizes](https://maoni0.medium.com/dynamically-adapting-to-application-sizes-2d72fcb6f1ea) — author's explanation

### Configuration Knobs

| Setting | Config key | Env var | Default | Notes |
|---------|-----------|---------|---------|-------|
| Heap hard limit | `System.GC.HeapHardLimit` | `DOTNET_GCHeapHardLimit` | unset | Absolute byte limit. Hex value in env var. |
| Heap hard limit % | `System.GC.HeapHardLimitPercent` | `DOTNET_GCHeapHardLimitPercent` | 75% in containers | % of physical memory |
| High memory threshold | `System.GC.HighMemoryPercent` | `DOTNET_GCHighMemPercent` | 90% | Triggers aggressive collection |
| Conserve memory | `System.GC.ConserveMemory` | `DOTNET_GCConserveMemory` | 0 | 1-9; higher = more conservation. Auto-compacts LOH. |
| DATAS | `System.GC.DynamicAdaptationMode` | `DOTNET_GCDynamicAdaptationMode` | 0 (.NET 8), 1 (.NET 9+) | — |
| Retain VM | `System.GC.RetainVM` | `DOTNET_GCRetainVM` | false | Keep decommitted segments on standby |
| LOH threshold | `System.GC.LOHThreshold` | `DOTNET_GCLOHThreshold` | 85,000 bytes | Can only be raised |
| Heap count | `System.GC.HeapCount` | `DOTNET_GCHeapCount` | = logical CPUs | Server GC only |

.NET 8 added `GC.RefreshMemoryLimit()` — can adjust `GCHeapHardLimit` at runtime via `AppContext.SetData("GCHeapHardLimit", (ulong)limit)` then `GC.RefreshMemoryLimit()`.

> [Microsoft Learn — GC config settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector) — comprehensive reference
> [Microsoft Learn — GC.RefreshMemoryLimit](https://learn.microsoft.com/en-us/dotnet/api/system.gc.refreshmemorylimit) — dynamic adjustment

---

## Large Object Heap

Any object >= 85,000 bytes goes on the LOH. For `double[]`, this is ~10,600 elements. For `byte[]`, exactly 85,000 elements. The LOH is collected only during Gen 2 (full) GCs and is **swept, not compacted** by default — dead objects become a free list.

### Fragmentation

Long-running processes can experience OOM after days despite sufficient total free memory — contiguous blocks are fragmented. Temporary large objects are the worst case: they trigger expensive Gen 2 GCs and their allocation/deallocation pattern fragments the LOH.

### Mitigations

| Approach | Mechanism | Trade-off |
|----------|-----------|-----------|
| `ArrayPool<T>.Shared` | Pool reusable arrays, avoid repeated LOH allocations | Rented arrays may be larger than requested |
| `GCConserveMemory=5-9` | Auto-compacts LOH when fragmentation exceeds threshold | More frequent compaction (expensive) |
| `RecyclableMemoryStream` | Pools underlying byte buffers for stream operations | Configuration complexity |
| `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` | One-shot LOH compaction on next full blocking GC | Resets after one compaction; must re-set |

> [Microsoft Learn — Large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap) — LOH mechanics
> [Adam Sitnik — Pooling large arrays with ArrayPool](https://adamsitnik.com/Array-Pool/) — patterns

---

## DuckDB in .NET

DuckDB defaults to 80% of physical RAM for its buffer pool. Spill-to-disk is available when the buffer manager exceeds its limit, but non-buffer allocations (vectors, query results) live outside the buffer manager and can exceed the limit.

### allocator_flush_threshold

Default: 128 MB. Affects **all native allocations**, not just the buffer manager. Triggers during worker thread idle periods (after 500ms without tasks).

- **jemalloc path:** Purges the thread's arena if peak allocation was *below* the threshold. This is counterintuitive — a higher threshold means more threads qualify for flushing, so higher = more aggressive.
- **glibc path:** Sets the `pad` parameter to `malloc_trim`.

Sibling setting `allocator_bulk_deallocation_flush_threshold` (default: 512 MB) is buffer-manager-specific — triggers unconditional arena purge during eviction operations when a bulk deallocation exceeds the threshold.

> [DuckDB — allocator source](https://github.com/duckdb/duckdb/blob/main/src/common/allocator.cpp) — flush dispatch to jemalloc/glibc
> [DuckDB — task_scheduler.cpp](https://github.com/duckdb/duckdb/blob/main/src/parallel/task_scheduler.cpp) — idle thread trigger

### DELETE FROM Behavior

([duckdb/duckdb #9263](https://github.com/duckdb/duckdb/issues/9263), closed NOT_PLANNED):

- **In-memory databases:** `DELETE FROM` marks rows as deleted but **never reclaims memory**. Row groups are vacuumed during checkpointing, which never happens for in-memory databases. `VACUUM` is PostgreSQL-compat only — it does nothing. No workaround other than `DROP TABLE` + recreate.
- **On-disk databases:** `CHECKPOINT` triggers vacuuming of deleted rows.
- `UPDATE` and `INSERT ... ON CONFLICT` use delete-and-insert internally — same issue.
- `DROP TABLE` may not fully free memory immediately either ([#14087](https://github.com/duckdb/duckdb/issues/14087)). Setting `allocator_background_threads=true` enables gradual release via jemalloc background purging (1-30 seconds).

### DuckDB.NET and GC Pressure

DuckDB.NET does not call `GC.AddMemoryPressure` — confirmed by examining the [Giorgi/DuckDB.NET](https://github.com/Giorgi/DuckDB.NET) source. All native objects use `SafeHandleZeroOrMinusOneIsInvalid` with proper `ReleaseHandle()` implementations, but the GC has zero visibility into the size of native allocations behind those handles.

**Practical consequence:** If DuckDB holds hundreds of MB natively while the managed heap remains small, the GC may delay Gen2 collections and SafeHandle finalization. Consider calling `GC.AddMemoryPressure` when allocating large native resources.

> [DuckDB — Memory Management](https://duckdb.org/2024/07/09/memory-management) — architecture, `duckdb_memory()`, spilling
> [DuckDB — Environment tuning](https://duckdb.org/docs/stable/guides/performance/environment) — minimum 125 MB per thread
> [DuckDB — Reclaiming Space](https://duckdb.org/docs/stable/operations_manual/footprint_of_duckdb/reclaiming_space) — checkpoint-based vacuuming

---

## ONNX Runtime in .NET

### Arena Allocator Strategies

| Property | kNextPowerOfTwo (default) | kSameAsRequested |
|----------|--------------------------|-------------------|
| Extension sizing | Doubles each time (1→2→4→8 MB) | Exact request size (rounded to min alignment) |
| Over-allocation | Speculative, can 2-4x actual need | Minimal |
| Bounds total growth | No (`memory_limit_` defaults to `size_t::max`) | No (same limit) |
| First region shrinkable | No (exempt from `Shrink()`) | Yes |
| initial_chunk_size respected | Yes | No (ignored) |

`kSameAsRequested` does **not** bound total arena growth — it only prevents speculative over-allocation. Both strategies share the same (effectively unbounded) `memory_limit_`. The key advantage: prevents the arena from growing faster than actual demand, and the first region is shrinkable.

> [ONNX Runtime — bfc_arena.cc](https://github.com/microsoft/onnxruntime/blob/main/onnxruntime/core/framework/bfc_arena.cc) — arena implementation

### Session.Dispose() — Two-Layer Problem

At the ONNX Runtime C++ level, `~BFCArena()` correctly frees all regions via `device_allocator_->Free()`. The native memory is released back to the **system allocator** (malloc/CRT). However, the system allocator (glibc on Linux, Windows CRT) may **retain freed pages** in its own heap arenas, keeping process RSS high. This is not an ORT leak — it's standard allocator behavior.

| Issue | Status | Finding |
|-------|--------|---------|
| [#14466](https://github.com/microsoft/onnxruntime/issues/14466) — GPU memory retained | Fixed (PR #15040) | External data files not freed on dispose |
| [#7067](https://github.com/microsoft/onnxruntime/issues/7067) — 80MB model → 1.4GB allocation | Closed stale, unfixed | Protobuf deserialization overhead |
| [#5292](https://github.com/microsoft/onnxruntime/issues/5292) — unbounded memory growth | Workaround | Zipmap in classification models; disable at export |
| [#26831](https://github.com/microsoft/onnxruntime/issues/26831) — session create/destroy leak | Open (Dec 2025) | System allocator retains freed pages; `malloc_trim(0)` helps on Linux, no Windows equivalent |
| [#5176](https://github.com/microsoft/onnxruntime/issues/5176) — C# wrapper memory growth | Fixed | GC timing, not a leak; `GC.Collect()` after dispose stabilized |

No E5/sentence-transformer-specific memory issues found. These are standard BERT-based models. No systematic C#-specific wrapper leak confirmed beyond the #14466 external data bug.

### Arena Shrinkage

`memory.enable_memory_arena_shrinkage` is set on `RunOptions` before `Run()`. Operates at region granularity — if any chunk in a region is in use, the entire region is retained.

The ORT team explicitly recommends **not using shrinkage for CPU** ([Issue #23339](https://github.com/microsoft/onnxruntime/issues/23339)): "For CPU we recommend disabling the arena altogether and see if default allocator does a better job (it often does)." Real-world reports confirm it is effective for GPU but not CPU.

**Recommended pattern for bursty workloads:** Arena off for a long-lived base session (low memory when idle), short-lived arena-enabled session for batch operations (throughput when active), with the session cached on a sliding expiry.

**On Windows, once ONNX arena memory is allocated, the process RSS may not decrease even after session disposal.** If memory must truly be reclaimed, the process must exit.

> [ONNX Runtime — Memory tuning](https://onnxruntime.ai/docs/performance/tune-performance/memory.html) — arena behavior
> [ONNX Runtime — OrtValue](https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.OrtValue.html) — disposal requirements
> [ONNX Runtime Issue #26831](https://github.com/microsoft/onnxruntime/issues/26831) — system allocator retention

---

## Roslyn for Analysis

Roslyn is managed-only but memory-heavy. Using Roslyn for **analysis only** (no Emit) avoids the 10-40 MB per-Emit overhead but still carries significant costs.

### Analysis-Only Memory Profile

| Component | Estimated Cost | Notes |
|-----------|---------------|-------|
| `CSharpCompilation.Create` | ~10 MB baseline | Minimal references; ~30-80 MB for a real 50-100 file project with framework refs |
| `GetSemanticModel()` per file | 0.5-5 MB | Bound trees for method bodies; with nullable enabled, full method bodies must be bound |
| Syntax trees | ~20-30% of heap in large solutions | Red-green tree architecture; recoverable to disk for files > 4 KB |
| Metadata references | Shared within workspace | Instance identity matters for caching — `MSBuildWorkspace` handles this internally |

### Red-Green Trees

Roslyn uses immutable "green" nodes (position-independent, shareable within incremental edits) wrapped by "red" nodes (API-facing, position-aware). Cross-file green node sharing does not occur — each file's tree is independent. For files > 4 KB, Roslyn can serialize trees to disk and re-parse when needed (recoverable tree mechanism).

> [Eric Lippert — Persistence, facades and Roslyn's red-green trees](https://ericlippert.com/2012/06/08/red-green-trees/) — canonical architecture explanation

### Semantic Model Cost

`GetSemanticModel()` is lazy but expensive once queried. Creating the `SemanticModel` is cheap; querying it triggers binding. With C# 8+ nullable reference types, the compiler must bind **entire method bodies** for nullable flow analysis, making binding more expensive than pre-nullable C#. Calling `Compilation.GetSemanticModel()` (vs receiving one through an analyzer callback) forces a complete rebind — [4-5x slower](https://github.com/dotnet/roslyn-analyzers/issues/3114).

### MSBuildWorkspace vs AdhocWorkspace

MSBuildWorkspace has higher baseline cost (MSBuild project evaluation in addition to Roslyn structures, essentially double-parsing project files) but provides automatic reference resolution, NuGet handling, and source generator execution. [Loading Roslyn.sln takes ~4 minutes](https://github.com/dotnet/roslyn/issues/23823). AdhocWorkspace avoids MSBuild overhead but requires manual reference configuration. For accurate semantic analysis, MSBuildWorkspace is the correct choice.

### Mitigations for Long-Running Processes

- **Cap concurrent project sessions** — `IMemoryCache` with a `SizeLimit` to bound concurrent compilations.
- **Release compilation resources explicitly** — null out compilation references after analysis to prevent bound tree accumulation. This is the single most important optimization.
- **Limit concurrent loads** — `SemaphoreSlim` to prevent multiple projects loading simultaneously.
- **Expiration** — sliding + absolute expiration on cached sessions.
- **Workspace reuse** — keep workspace and metadata references alive within a session for reuse across files in the same project.

### Source Generator Memory

Generated documents are fully materialized as in-memory `SyntaxTree` objects — no recoverable-tree mechanism. Cost depends entirely on the target project's generators: modest generators add 5-50 MB; pathological cases (System.Text.Json generating ~3,500 files) consumed [4-7 GB](https://github.com/dotnet/runtime/issues/68353). To skip generators: `project.WithAnalyzerReferences(Enumerable.Empty<AnalyzerReference>())` — generated types will show as unresolved but core structural analysis remains accurate.

> [dotnet/roslyn #55518](https://github.com/dotnet/roslyn/issues/55518) — no official MSBuild property to disable generators (open since 2021)

### Memory Envelope

8 concurrent project sessions (each holding `MSBuildWorkspace` + `Project`) can consume 240-640 MB of managed heap (30-80 MB per project). Bounded by cache size but significant on memory-constrained machines.

> [Roslyn Issue #40300](https://github.com/dotnet/roslyn/issues/40300) — syntax tree caching accounts for 20-30% of heap
> [Roslyn Issue #39840](https://github.com/dotnet/roslyn/issues/39840) — SemanticModel caching, nullable binding cost
> [Roslyn — Performance considerations for large solutions](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Performance-considerations-for-large-solutions.md) — official guidance
> [OmniSharp Issue #2418](https://github.com/OmniSharp/omnisharp-roslyn/issues/2418) — 1-3 GB growth on large solutions

---

## Process Lifecycle on Windows

### Child Process Cleanup

`Process.Start()` with `UseShellExecute = false` does not create any parent-child lifetime binding. If the parent crashes, child processes continue running indefinitely. `dotnet watch` compounds this by creating a process subtree that is [not automatically killed](https://github.com/dotnet/sdk/issues/8610) when the parent terminates.

Each orphaned process holds its own GC heap + any native memory pools. A handful of orphans from a memory-hungry process can consume tens of gigabytes.

### Job Objects

Windows Job Objects can enforce `KillOnJobClose` — all child processes die when the parent exits. The `Meziantou.Framework.Win32.Jobs` NuGet package provides a wrapper:

```csharp
var job = new JobObject();
job.SetLimits(new JobObjectLimits { Flags = JobObjectLimitFlags.KillOnJobClose });
job.AssignProcess(Process.GetCurrentProcess());
```

The .NET runtime [honors Job Objects](https://github.com/dotnet/designs/blob/main/accepted/2019/support-for-memory-limits.md) for GC memory limit detection on Windows, just as it honors cgroups on Linux.

> [dotnet/sdk #8610](https://github.com/dotnet/sdk/issues/8610) — child processes not killed on parent exit
> [Meziantou.Framework.Win32.Jobs](https://www.nuget.org/packages/Meziantou.Framework.Win32.Jobs) — Job Object wrapper
> [dotnet/runtime #101985](https://github.com/dotnet/runtime/issues/101985) — proposed KillOnParentDeath for ProcessStartInfo

### Shutdown Mechanics

`Host.StopAsync` uses `HostOptions.ShutdownTimeout` (default: **5 seconds**). Hosted services are stopped **sequentially** in reverse registration order. A single slow service blocks all subsequent services. Foreground threads (non-`IsBackground`) prevent process exit even after `StopAsync` returns.

> [Andrew Lock — Extending the shutdown timeout](https://andrewlock.net/extending-the-shutdown-timeout-setting-to-ensure-graceful-ihostedservice-shutdown/) — timeout mechanics
> [dotnet/runtime #68036](https://github.com/dotnet/runtime/issues/68036) — sequential hosted service shutdown

---

## Memory Pressure Patterns

Common causes of unbounded memory growth in long-running .NET processes:

| Pattern | Mechanism | Detection |
|---------|-----------|-----------|
| Static collections | `static ConcurrentDictionary` without eviction grows forever | `dumpheap -stat` → growing type counts |
| Event handler leaks | Publisher → subscriber strong reference prevents collection | VS 2022 17.9+ Event Handler Leak detection |
| String interning | `string.Intern()` on dynamic data creates uncollectable table | `dumpheap -type System.String` → large interned set |
| Finalizer queue backup | Single finalizer thread; blocking finalizers stall the queue | `dotnet-counters` → rising finalization queue |
| Unbounded caches | `IMemoryCache` without size limits | Monitor cache entry count |
| Undisposed native wrappers | SafeHandle wrappers hold large native allocations | `Process.PrivateMemorySize64` growing, `gc-heap-size` stable |
| Closures | Lambdas capture outer variables, keeping objects alive | `gcroot` in dotnet-dump |
| Async state machine accumulation | Long-lived async operations promote state machines to Gen2 | Gen2 fragmentation growth |

> [Michael's Coding Spot — 8 Ways You can Cause Memory Leaks](https://michaelscodingspot.com/ways-to-cause-memory-leaks-in-dotnet/) — comprehensive catalog
> [Microsoft Learn — Debug a memory leak](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-memory-leak) — diagnostic workflow

---

## Profiling Toolkit

### Live Monitoring: dotnet-counters

```bash
dotnet-counters monitor --process-id <PID> --counters System.Runtime
```

Key counters for memory investigation:

| Counter | Key name | What it reveals |
|---------|----------|----------------|
| GC Heap Size (MB) | `gc-heap-size` | Managed heap trend |
| Gen 0/1/2 GC Count | `gen-0-gc-count` etc. | Collection frequency |
| LOH Size | `loh-size` | Large object accumulation |
| Allocation Rate | `alloc-rate` | Allocation pressure |
| % Time in GC | `time-in-gc` | GC overhead |
| Working Set (MB) | `working-set` | Total process memory (managed + native) |
| GC Fragmentation | `gc-fragmentation` | Heap fragmentation (.NET 5+) |
| GC Committed Bytes | `gc-committed` | Committed virtual memory (.NET 6+) |
| ThreadPool Queue Length | `threadpool-queue-length` | Starvation indicator |

.NET 9 added OpenTelemetry-compatible meters under `System.Runtime` (`dotnet.gc.collections`, `dotnet.gc.heap.total_allocated`, `dotnet.process.memory.working_set`, etc.).

> [Microsoft Learn — Well-known EventCounters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters) — complete counter reference
> [Microsoft Learn — Runtime metrics (.NET 9)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime) — modern meters

### GC Heap Snapshot: dotnet-gcdump

Captures managed heap snapshot by triggering a Gen 2 GC. Produces small `.gcdump` files (vs. multi-GB full dumps). Near-zero performance impact.

```bash
dotnet-gcdump collect --process-id <PID> --output snapshot.gcdump
dotnet-gcdump report snapshot.gcdump   # CLI summary
```

**Differential analysis:** Capture before and after suspected leak. Open both in PerfView, use "Compare Snapshots" to identify growing types.

**Limitations:** Only managed heap. Large heaps may overflow eventing buffers, producing incomplete graphs — fall back to full dumps. Analysis requires Windows (PerfView or Visual Studio).

**Cross-platform analysis:** `dotnet-gcdump report` (built-in) works cross-platform but only produces flat type statistics — no object graph or root analysis. Community tools: [dotnet-heapview](https://github.com/1hub/dotnet-heapview) (simple viewer), [gcdump-analyze](https://github.com/jonathanpeppers/gcdump-analyze) (textual/AI-friendly). No official cross-platform GUI viewer announced.

> [Microsoft Learn — dotnet-gcdump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump) — usage and limitations
> [Stefan Geiger — gcdump vs dump](https://www.stefangeiger.ch/2020/04/21/dotnet-diagnostics-tools-gcdump-vs-dump.html) — comparison

### Full Dump: dotnet-dump

For when you need native memory analysis, full thread state, or the heap is too large for gcdump.

```bash
dotnet-dump collect --process-id <PID>
dotnet-dump analyze <dump-file>
```

Key SOS commands for memory leak investigation:

| Command | Purpose |
|---------|---------|
| `dumpheap -stat` | All types with count and total size |
| `dumpheap -type <TypeName>` | Instances of a specific type |
| `dumpobj <address>` | Inspect a specific object's fields |
| `gcroot <address>` | Trace why an object is kept alive |
| `eeheap -gc` | GC heap segments per generation |
| `threadpool` | ThreadPool statistics |
| `syncblk` | Sync block table (lock contention) |

**Workflow:** `dumpheap -stat` → find suspicious types → `dumpheap -type` → pick instance → `gcroot` → find retention path.

**Limitation:** Dumps are not portable across platforms. Process dumps on Windows cannot be analyzed on Linux.

> [Microsoft Learn — dotnet-dump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump) — usage
> [Tess Ferrandez — Debugging a .NET Core memory issue](https://www.tessferrandez.com/blog/2021/03/18/debugging-a-netcore-memory-issue-with-dotnet-dump.html) — practical walkthrough

### Tracing: dotnet-trace

```bash
# GC collection events only (low overhead)
dotnet-trace collect --process-id <PID> --profile gc-collect

# GC + allocation sampling
dotnet-trace collect --process-id <PID> --profile gc-verbose

# CPU sampling (thread time)
dotnet-trace collect --process-id <PID> --profile dotnet-sampled-thread-time

# Convert for Speedscope
dotnet-trace convert trace.nettrace --format Speedscope
```

CLR event keyword aliases: `gc`, `jit`, `contention`, `exception`, `threading`, `gcheapdump`, `gcsampledobjectallocationhigh`.

**SampleProfiler safe-point bias:** CPU sampling uses GC suspension infrastructure — threads stop at safe points (typically method returns), not at actual execution point. Leaf methods in tight computational loops are invisible; samples are attributed to callers. Not fixed in .NET 9/10 ([dotnet/runtime#45518](https://github.com/dotnet/runtime/issues/45518)). For accurate CPU profiling: ETW via PerfView (Windows, requires admin) or `perf` + `perfcollect` (Linux, requires root). The bias is minimal for I/O-heavy or framework-heavy workloads.

> [Microsoft Learn — dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) — profiles and providers

### Programmatic Diagnostics

| API | What it provides |
|-----|-----------------|
| `GC.GetGCMemoryInfo()` | Per-generation heap sizes, fragmentation, pause durations, compaction status |
| `GC.GetTotalMemory(false)` | Approximate managed heap bytes |
| `GC.GetTotalAllocatedBytes(precise)` | Total bytes allocated since process start |
| `GC.GetTotalPauseDuration()` | Total GC pause time (.NET 8+) |
| `GC.CollectionCount(generation)` | Number of GCs per generation |
| `Process.GetCurrentProcess().WorkingSet64` | Physical memory pages |
| `Process.GetCurrentProcess().PrivateMemorySize64` | Private committed memory (managed + native) |

`GC.GetGCMemoryInfo()` returns zeros if called before any GC has occurred. Use `GC.GetGCMemoryInfo(GCKind.FullBlocking)` to query specifically about full blocking collections.

> [Microsoft Learn — GC.GetGCMemoryInfo](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getgcmemoryinfo) — comprehensive struct
> [.NET Blog — The updated GetGCMemoryInfo API](https://devblogs.microsoft.com/dotnet/the-updated-getgcmemoryinfo-api-in-net-5-0-and-how-it-can-help-you/) — usage patterns

### BenchmarkDotNet

For micro-benchmarking. See the **Writing Benchmarks** section for full patterns, methodology, anti-patterns, and MemoryDiagnoser interpretation.

> [BenchmarkDotNet — Docs](https://benchmarkdotnet.org/) — comprehensive reference

---

## Diagnostic Approach Comparison

| Need | Tool | Measured Overhead | What it shows |
|------|------|-------------------|---------------|
| Live memory trend | `dotnet-counters` | Negligible for `System.Runtime`; high-frequency custom counters can cause [52% throughput loss](https://github.com/dotnet/aspnetcore/issues/50412) at extreme RPS | GC heap, working set, allocation rate |
| Managed heap snapshot | `dotnet-gcdump` | Full Gen 2 GC pause (duration ∝ heap size); up to 256 MB collection buffer in target process; [can cause OOM](https://github.com/dotnet/diagnostics/issues/2038) on constrained heaps | Type counts, sizes, retained graphs |
| Full managed + native analysis | `dotnet-dump` | Process paused during capture; dump file ≈ process memory size | Everything, but large files, not portable |
| GC collection behavior | `dotnet-trace --profile gc-collect` | Lowest cost GC profile; subscribes to collection start/end only | Collection timing, generations |
| Allocation source tracking | `dotnet-trace --profile gc-verbose` | ~2% realistic; up to ~20% under extreme allocation pressure ([dotnet/runtime#49424](https://github.com/dotnet/runtime/issues/49424)) | Where allocations happen |
| CPU hot paths | `dotnet-trace --profile dotnet-sampled-thread-time` | Not officially quantified; 1ms fixed rate, uses GC suspension; known [safe-point bias](https://github.com/dotnet/runtime/issues/45518) | Stack sampling |
| DuckDB memory breakdown | `SELECT * FROM duckdb_memory()` | Negligible | Buffer manager by category |
| Micro-benchmarking | BenchmarkDotNet `[MemoryDiagnoser]` | N/A (test harness) | Allocations per operation |
| VS interactive debugging | Memory snapshots, Object Allocation Tracking | Variable | Visual graphs, event handler leak detection |

**EventPipe filtering caveat:** EventPipe/LTTNG filters events **after** data is generated — filtered-out events still pay full serialization cost. This caused a measured [60% response time increase](https://github.com/dotnet/runtime/issues/12204) when allocation events were inadvertently generated.

---

## gRPC Performance

### Buffer Management

Default incoming message limit: 4 MB. Entire messages are loaded into memory before gRPC can process them. A `byte[]` >= 85,000 bytes goes on the LOH.

| Practice | Mechanism |
|----------|-----------|
| Keep binary payloads < 85 KB | Avoid LOH allocations |
| Use streaming for large data | Chunks avoid single large allocation |
| `UnsafeByteOperations.UnsafeWrap()` | Create ByteString without copying |
| HTTP/2 flow control tuning | `InitialConnectionWindowSize`, `InitialStreamWindowSize` on Kestrel |

### Unix Domain Sockets for IPC

For same-machine communication, UDS avoids TCP/TLS overhead. ~100 microsecond unary-call latency. Supported on Windows 10+.

> [Microsoft Learn — gRPC performance best practices](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance) — comprehensive guide
> [Microsoft Learn — gRPC IPC with UDS](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds) — Unix Domain Socket setup

### Streaming Call Lifecycle

Undisposed streaming calls leak both client and server resources. `WriteAsync` is not thread-safe on a single stream — use `Channel<T>` to marshal messages from multiple threads. Complete streams with `RequestStream.CompleteAsync()` rather than relying on cancellation.

---

## Concurrency Patterns

### System.Threading.Channels

Bounded channels provide backpressure. Unbounded channels should be reserved for lightweight signals (e.g. epoch numbers, completion notifications) — never for data that grows proportionally with input.

`SingleReader = true` / `SingleWriter = true` eliminates locks on the respective path. .NET 9 further optimized channel internals with lock-free algorithms.

> [Microsoft Learn — Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels) — bounded vs unbounded, backpressure

### ConcurrentDictionary

Per-entry memory on x64:

| Collection | Per-entry | Layout |
|-----------|-----------|--------|
| `Dictionary<K,V>` | ~28 bytes | `Entry` struct in flat array (24 bytes + amortized bucket) |
| `ConcurrentDictionary<K,V>` | ~48 bytes | `Node` class (16-byte object header + key + value + next + hashcode + padding) |

That's ~20 extra bytes per entry, primarily from object header overhead. Additionally, every value update [allocates a new Node object](https://blog.getpaint.net/2017/06/30/concurrentdictionary-allocates-a-lot/) to avoid torn reads — significant GC pressure under write-heavy workloads. With 500K string entries, total allocation was [18 MB for Dictionary vs 110 MB for ConcurrentDictionary](https://github.com/dotnet/runtime/issues/667) (~6x including resize churn; ~2x steady-state).

`GetOrAdd` calls `valueFactory` **outside the lock** — multiple threads may invoke it concurrently for the same key.

For read-heavy, rarely mutated data, `FrozenDictionary` (.NET 8+) is 47% faster for reads on average and uses less memory. Trade-off: expensive construction, completely immutable.

> [dotnet/runtime #667](https://github.com/dotnet/runtime/issues/667) — ConcurrentDictionary memory overhead
> [Dave Callan — FrozenDictionary benchmarks](https://davecallan.com/dotnet-8-frozendictionary-benchmarks/) — read performance comparison

### Async Patterns

Every async method generates a state machine struct. When the method doesn't complete synchronously, this struct is boxed (~72 bytes per invocation in .NET 9). `ValueTask<T>` avoids heap allocation when the result is already available synchronously — use on hot paths that return synchronously 80-90% of the time.

Always use `TaskCreationOptions.RunContinuationsAsynchronously` with `TaskCompletionSource<T>` to prevent thread hijacking.

> [.NET Blog — Understanding ValueTask](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/) — when to use
> [.NET Blog — Async ValueTask Pooling](https://devblogs.microsoft.com/dotnet/async-valuetask-pooling-in-net-5/) — PoolingAsyncValueTaskMethodBuilder

### ThreadPool

Thread injection rate: ~1 new thread per 500 ms after minimum count is reached. Injection stops when CPU usage exceeds 80%. Starvation signals: `threadpool-queue-length` rising, `threadpool-thread-count` slowly increasing, CPU well below 100%.

The sync-over-async pattern (`Task.Result`, `Task.Wait()`, `.GetAwaiter().GetResult()`) is the primary cause of starvation. `SetMinThreads` is a band-aid; async-all-the-way is the fix.

> [Microsoft Learn — Debug ThreadPool Starvation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation) — diagnosis workflow

---

## Memory-Efficient Patterns

| Pattern | Mechanism | When to use |
|---------|-----------|-------------|
| `Span<T>` / `Memory<T>` | Zero-copy slicing of arrays, strings, native memory | Parsing, string processing |
| `ArrayPool<T>.Shared` | Pool reusable arrays, avoid LOH allocations | Any buffer > ~1 KB |
| `stackalloc` + fallback | Stack allocation for small buffers, ArrayPool for large | Hot-path small buffers |
| `RecyclableMemoryStream` | Pools underlying byte buffers for streams | Serialization, I/O |
| `string.Create` | Write directly into string buffer, no intermediate allocation | String construction |
| `FrozenDictionary` / `FrozenSet` | Immutable, read-optimized collections | Write-once lookup tables |
| `GC.AddMemoryPressure` | Inform GC about native allocations behind managed wrappers | Native interop wrappers |

---

## Analyzing Performance in Running Code

### Instrumentation

Two APIs matter for new code. Both are zero-overhead when no listener is attached.

**System.Diagnostics.ActivitySource** (distributed tracing / spans):

```csharp
private static readonly ActivitySource s_source = new("MyApp.Pipeline");

using var activity = s_source.StartActivity("ProcessFile");
activity?.SetTag("file.path", filePath);
activity?.SetTag("file.size", fileSize);
```

`StartActivity()` returns `null` when no listener is registered — no Activity object created, no allocation. Use `?.` everywhere. One `ActivitySource` per component, stored as `static readonly`.

**System.Diagnostics.Metrics** (counters, histograms):

```csharp
private static readonly Meter s_meter = new("MyApp.Pipeline");
private static readonly Counter<long> s_files = s_meter.CreateCounter<long>("files.processed");
private static readonly Histogram<double> s_duration = s_meter.CreateHistogram<double>("files.duration", "ms");

s_files.Add(1, new KeyValuePair<string, object?>("format", "csharp"));
s_duration.Record(elapsed.TotalMilliseconds);
```

Advantages over the older EventCounter API: histograms, percentiles, multi-dimensional tags, multiple simultaneous listeners, nanosecond-per-measurement listener overhead. `System.Diagnostics.Metrics` is the recommended API for new work; EventCounters are maintained but not receiving new investment.

Both APIs feed into OpenTelemetry — instrumentation uses `System.Diagnostics`, export uses the OpenTelemetry SDK. Application code never depends on vendor-specific APIs.

> [Microsoft Learn — Metrics instrumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-collection) — Metrics API guide
> [Microsoft Learn — Distributed tracing instrumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs) — ActivitySource guide
> [Microsoft Learn — Compare metric APIs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/compare-metric-apis) — EventCounters vs Metrics

### Hot Path Identification

**CPU hot spots:**

```bash
# 30-second CPU profile, output as flame graph
dotnet-trace collect -p <PID> --format Speedscope --duration 00:00:00:30
# Open result at https://www.speedscope.app

# Or: CLI top-N without a GUI
dotnet-trace report trace.nettrace topN -n 10           # exclusive time
dotnet-trace report trace.nettrace topN -n 10 --inclusive  # inclusive time
```

For PerfView analysis: open `.nettrace` → CPU Stacks → select process → "By Name" tab for exclusive CPU time per method. Export to SpeedScope for interactive flame graphs.

**Flame graph reading:** Y-axis = call stack depth (bottom = entry, top = leaf). X-axis width = time on CPU. Wider = hotter. Color is arbitrary. Follow the widest boxes from bottom to top.

**Allocation hot spots:**

```bash
# Sampled allocations (~1 event per 100 KB allocated)
dotnet-trace collect -p <PID> --profile gc-verbose --duration 00:00:00:30
```

Open in PerfView → GC Heap Alloc Stacks. Shows which types were allocated, how much, and from which call stacks. "By Name" tab sorts by type for highest-volume allocations.

**Contention hot spots:**

```bash
# Monitor lock contention events with stacks
dotnet-trace collect -p <PID> --clrevents contention+stack --clreventlevel informational

# Wait handle events (.NET 9+) — covers Monitor, ManualResetEvent, Task.Wait
dotnet-trace collect -p <PID> --clrevents waithandle+stack --clreventlevel verbose
```

> [Brendan Gregg — CPU Flame Graphs](https://www.brendangregg.com/FlameGraphs/cpuflamegraphs.html) — flame graph interpretation
> [Profiling .NET with PerfView + SpeedScope](https://adamsitnik.com/speedscope/) — practical workflow

### Production Safety

**Safe for production:**

| Technique | Overhead |
|-----------|----------|
| `dotnet-counters` | Negligible — reads existing counters |
| `dotnet-trace --profile gc-collect` | Very low — collection events only |
| `dotnet-trace` default CPU sampling | ~5-10% CPU |
| PerfView CPU sampling (10ms interval) | ~3% |
| `dotnet-monitor` idle | Negligible |
| Continuous profilers (Datadog, Pyroscope) | 1-5% CPU |
| OpenTelemetry with head-based sampling | Low |

**Test environment only:**

| Technique | Why |
|-----------|-----|
| `dotnet-trace --profile gc-verbose` | Allocation sampling switches CLR to slower allocators |
| Both `GCSampledObjectAllocationHigh` + `Low` enabled | Event on every allocation |
| dotTrace Tracing / Line-by-line mode | Instruments method entry/exit or every statement |
| VS .NET Object Allocation Tracking | Can significantly slow the app |

**Critical notes:**
- Allocation event keywords switch the CLR to "slower" allocators when enabled. Always verify impact before production use.
- Stopping a trace on large applications can take minutes — the runtime must send the type cache for all managed code.
- If the process emits events faster than disk write speed, events are dropped. Increase `--buffersize` (default 256 MB) or reduce enabled events.

> [Microsoft Learn — dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) — profiles and providers
> [Microsoft Learn — dotnet-monitor](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-monitor) — production diagnostics

### Contention Analysis

The runtime emits `ContentionStart`/`ContentionStop` events for `Monitor` locks (C# `lock` keyword) with contention duration in nanoseconds and lock owner thread ID.

```bash
dotnet-counters monitor -p <PID> \
  --counters System.Runtime[monitor-lock-contention-count,threadpool-thread-count,threadpool-queue-length]
```

**SemaphoreSlim is invisible** to standard contention diagnostics — the runtime only emits contention events for `Monitor` locks. Diagnose SemaphoreSlim bottlenecks via CPU profiling (time in `WaitAsync`/`Release`) or custom metrics on wait duration and queue depth.

**.NET 9 added `WaitHandleWaitStart`/`WaitHandleWaitStop` events** (keyword `waithandle`, `0x40000000000`) covering `Monitor.Wait`, `Monitor.Enter`, `ManualResetEvent.WaitOne`, and `Task.Wait`. These are at Verbose level — use for targeted investigation, not continuous monitoring.

| Pattern | Runtime Events | Diagnosis |
|---------|---------------|-----------|
| `lock (obj)` / `Monitor.Enter` | ContentionStart/Stop | Standard contention events |
| `SemaphoreSlim(1,1).WaitAsync()` | None | CPU profiling, custom metrics |
| `Channel<T>` bounded | None | Custom metrics on channel fullness |
| `ReaderWriterLockSlim` | ContentionStart/Stop (internal Monitor) | Standard contention events |

> [Microsoft Learn — Contention events](https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-contention-events) — event schema
> [Microsoft Learn — Wait handle events (.NET 9)](https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-wait-handle-events) — new in .NET 9
> [Michael's Coding Spot — Debugging Lock Contention](https://michaelscodingspot.com/lock-contentions/) — practical guide

### EventPipe Provider Reference

The `Microsoft-Windows-DotNETRuntime` provider is used on all platforms (EventPipe intercepts the name cross-platform).

```bash
# Provider string syntax
# ProviderName[:Keywords[:Level[:KeyValueArgs]]]
Microsoft-Windows-DotNETRuntime:0x4000:4     # Contention at Informational
Microsoft-Windows-DotNETRuntime:3:4           # GC + GCHandle at Informational
```

| Keyword | Hex | What it captures |
|---------|-----|-----------------|
| `gc` | `0x1` | GC collection events |
| `contention` | `0x4000` | Monitor lock contention |
| `gcsampledobjectallocationhigh` | `0x200000` | Sampled allocations (~100 KB interval) |
| `gcsampledobjectallocationlow` | `0x2000000` | Lower-frequency allocation sampling |
| `waithandle` | `0x40000000000` | Wait handle events (.NET 9+) |
| `allocationsampling` | `0x80000000000` | Newer allocation keyword (.NET 10) |
| `stack` | `0x40000000` | Capture stacks on events |
| `type` | `0x80000` | Type information |
| `gcheapandtypenames` | `0x1000000` | Type names for allocation events |

Levels: LogAlways (0), Critical (1), Error (2), Warning (3), Informational (4), Verbose (5).

> [Microsoft Learn — Well-known EventCounters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters) — counter reference
> [Microsoft Learn — Runtime metrics (.NET 9)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime) — modern meters

### dotnet-monitor for Automated Diagnostics

Sidecar diagnostic tool for production. Exposes .NET CLI diagnostics via HTTP API. Trigger-based collection captures traces/dumps automatically when conditions are met.

```json
{
  "CollectionRules": {
    "HighCpuRule": {
      "Trigger": {
        "Type": "EventCounter",
        "Settings": {
          "ProviderName": "System.Runtime",
          "CounterName": "cpu-usage",
          "GreaterThan": 80,
          "SlidingWindowDuration": "00:00:30"
        }
      },
      "Actions": [{ "Type": "CollectDump", "Settings": { "Type": "Full" } }]
    }
  }
}
```

Negligible overhead when idle. Works in containers (official Docker image). Supports both connect mode (attaches to existing processes) and listen mode (processes connect to it).

> [Microsoft Learn — dotnet-monitor](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-monitor) — documentation
> [dotnet-monitor GitHub](https://github.com/dotnet/dotnet-monitor) — source and releases

---

## Writing Benchmarks

### BenchmarkDotNet Essentials

The standard .NET benchmarking tool. Handles warmup, JIT tiering, outlier detection, and statistical analysis automatically.

**Diagnosers** add columns without affecting measurement (separate run):

| Diagnoser | Columns Added | Notes |
|-----------|---------------|-------|
| `[MemoryDiagnoser]` | Gen0, Gen1, Gen2, Allocated | Cross-platform, 99.5% accurate |
| `[ThreadingDiagnoser]` | Completed Work Items, Lock Contentions | .NET Core 3.0+ |
| `[EventPipeProfiler]` | Trace file output | Cross-platform profiling |
| `[NativeMemoryProfiler]` | Allocated native memory, Native memory leak | Windows only, ETW-based |
| `[ExceptionDiagnoser]` | Exception count | Uses FirstChanceException |
| `[DotTraceDiagnoser]` | .dtp snapshot file | Requires `BenchmarkDotNet.Diagnostics.dotTrace` NuGet |

**Parameterization:**

```csharp
[Params(100, 1000, 10_000)]
public int Size { get; set; }

[ParamsSource(nameof(Sizes))]
public int Size { get; set; }
public IEnumerable<int> Sizes => new[] { 64, 256, 1024 };

[ParamsAllValues]
public Algorithm Algo { get; set; }  // auto-enumerates enum values
```

Each parameter combination generates a separate benchmark row. 3 values × 3 values = 9 runs per method.

**Baselines and comparisons:**

```csharp
[Benchmark(Baseline = true)]
public int Original() => ProcessV1(_data);

[Benchmark]
public int Optimized() => ProcessV2(_data);
```

The `Ratio` column computes `mean(measurement[i] / baseline[i])` — per-pair division, not ratio of means. `RatioSD` > 0.05 means the comparison is noisy.

> [BenchmarkDotNet — Docs](https://benchmarkdotnet.org/) — comprehensive reference
> [BenchmarkDotNet — Diagnosers](https://benchmarkdotnet.org/articles/configs/diagnosers.html) — all diagnosers

### Reading MemoryDiagnoser Output

| Column | Meaning | Interpretation |
|--------|---------|----------------|
| `Gen0` | Gen 0 collections per 1,000 operations | `1.0000` = one collection per 1,000 ops |
| `Gen1` | Gen 1 collections per 1,000 operations | If equal to Gen0, objects are surviving Gen0 |
| `Gen2` | Gen 2 collections per 1,000 operations | Non-zero = red flag. Full GC per 1,000 ops. |
| `Allocated` | Bytes allocated per single operation | `0 B` is the gold standard for hot paths |

- `Gen1 > 0` means objects survive Gen 0 — investigate whether this is intentional caching or accidental long-lived temporaries.
- `Gen2 > 0` in a microbenchmark is a red flag. Gen 2 collections are stop-the-world events.
- In .NET 10, `Allocated = 0 B` where you expected allocations may be real — expanded escape analysis can stack-allocate delegates, arrays, and spans that were heap-allocated in .NET 9.

> [Adam Sitnik — The new MemoryDiagnoser](https://adamsitnik.com/the-new-Memory-Diagnoser/) — deep dive

### Methodology

BenchmarkDotNet's execution pipeline:

1. **Overhead** — measures empty method execution to establish baseline
2. **Pilot** — determines optimal invocation count per iteration
3. **Warmup** — 6-50 iterations until steady state (JIT tiering, CPU frequency, caches)
4. **Main** — 15-100 iterations with statistical convergence checks
5. **Post-processing** — outlier removal, distribution warnings

**When results are trustworthy:**
- `Error` should be < 5% of `Mean`
- `RatioSD` > 0.05 means the comparison is unstable
- If BenchmarkDotNet runs > 100 iterations, your benchmark has high variance — investigate
- Differences < 2% between benchmarks may be measurement noise

**Statistical columns:**

| Column | What it tells you |
|--------|-------------------|
| `Mean` | Average time per operation |
| `Error` | Half of the 99.9% confidence interval |
| `StdDev` | Standard deviation |
| `Median` | Middle value (robust to outliers) |
| `MValue` | Multimodal detection (> 4.2 = likely multimodal — benchmark has multiple performance modes) |

Don't manually set `LaunchCount`, `WarmupCount`, `IterationCount`, or `IterationTime` unless you have a specific reason. BenchmarkDotNet's auto algorithm achieves a good trade-off between precision and duration.

> [BenchmarkDotNet — How it works](https://benchmarkdotnet.org/articles/guides/how-it-works.html) — execution pipeline
> [BenchmarkDotNet — Statistics](https://benchmarkdotnet.org/articles/features/statistics.html) — statistical columns

### Anti-Patterns

**Stopwatch-based benchmarks lie.** No warmup (includes JIT tier 0 — 10-100x slower), no process isolation, no overhead subtraction, no outlier removal, no confidence interval, no protection against dead code elimination. Acceptable only for "is this 10x faster or 10x slower?" during development.

**Debug mode.** Debug builds disable all JIT optimizations. Results are meaningless. BenchmarkDotNet warns if you try. Always `dotnet run -c Release`.

**Dead code elimination:**

```csharp
// BAD: JIT may eliminate the entire computation
[Benchmark]
public void Bad() { Math.Exp(1); }

// GOOD: return the result — BenchmarkDotNet writes it to a volatile field
[Benchmark]
public double Good() => Math.Exp(1);
```

**Adding loops inside benchmarks.** BenchmarkDotNet already loops for you. Manual loops add overhead, prevent iteration control, and may trigger different JIT optimizations. If you must batch, use `[OperationsPerInvoke(N)]`.

**Setup allocation noise.** Allocate test data in `[GlobalSetup]`, not in the benchmark method — otherwise the setup allocation is measured.

```csharp
private byte[] _data;

[GlobalSetup]
public void Setup() => _data = new byte[Size];

[Benchmark]
public void Process() => DoWork(_data);  // only DoWork is measured
```

**Over-optimizing micro-benchmarks.** 10ns vs 15ns saves 5ns per call. At 1,000 calls × 10,000 requests/second = 50ms/second saved = 0.005% of wall clock. Profile first, optimize where it matters.

> [BenchmarkDotNet — Good Practices](https://benchmarkdotnet.org/articles/guides/good-practices.html) — anti-patterns
> [dotnet/performance — Design Guidelines](https://github.com/dotnet/performance/blob/main/docs/microbenchmark-design-guidelines.md) — official guidance

### Micro vs Macro Benchmarks

**Microbenchmarks** (BenchmarkDotNet's sweet spot): compare two implementations of the same operation, validate an optimization, measure allocation cost of a specific API. Misleading when the operation isn't on the hot path or system-level effects (I/O, contention, GC) dominate.

**Macrobenchmarks**: end-to-end scenarios including I/O, serialization, GC pressure. Use load testing tools (k6, bombardier) for HTTP, or BenchmarkDotNet with `RunStrategy.Monitoring` for methods > 100ms.

Microbenchmarks tell you the speed limit. Macrobenchmarks tell you the actual traffic speed. An operation that's 2x faster in a microbenchmark may produce no measurable improvement end-to-end if it's not on the critical path.

### .NET 9/10 Benchmarking Considerations

**Dynamic PGO (.NET 8+ default):** Profiles code during tier 0, optimizes during tier 1. Warmup matters more — the call patterns during warmup affect generated code. Compare with/without:

```csharp
AddJob(Job.Default.WithId("PGO-On"));
AddJob(Job.Default.WithEnvironmentVariable("DOTNET_TieredPGO", "0").WithId("PGO-Off"));
```

**Guarded Devirtualization (GDV):** The JIT profiles which types are used in virtual dispatch and emits specialized fast paths. If your benchmark always uses one concrete type, GDV will devirtualize it. In production with multiple types, the generic path may be taken. Results may be optimistic for polymorphic code.

**Stack allocation via escape analysis (.NET 10):** Delegates, arrays, and Span fields in structs can be stack-allocated when provably method-scoped. Code that allocated in .NET 9 may show `Allocated = 0 B` in .NET 10. This is real, not a broken diagnoser.

**NativeAOT benchmarking:** BenchmarkDotNet supports NativeAOT as a toolchain. No tiered compilation or dynamic PGO — all optimization at compile time. JIT typically achieves higher peak throughput for long-running processes (runtime profiling data); NativeAOT wins on startup time and deterministic performance.

> [Performance Improvements in .NET 9](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/) — JIT improvements
> [Performance Improvements in .NET 10](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/) — escape analysis, bounds checks

### Quick Reference Template

```csharp
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net10_0)]
[MarkdownExporterAttribute.GitHub]
public class MyBenchmarks
{
    private byte[] _data;

    [Params(100, 1000, 10_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        Random.Shared.NextBytes(_data);
    }

    [Benchmark(Baseline = true)]
    public int Baseline() => ProcessV1(_data);

    [Benchmark]
    public int Optimized() => ProcessV2(_data);
}

// Entry point
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

```bash
dotnet run -c Release -- --filter "*MyBenchmarks*"
```

---

## Profiling Workflow

`dotnet-counters` for trend → `dotnet-gcdump` for managed heap snapshot → `duckdb_memory()` for DuckDB buffer → `Process.PrivateMemorySize64 - GC.GetTotalMemory()` for native delta → `dotnet-dump` for root cause.

---

*DATAS is the strongest default for bursty workloads with idle periods on shared machines. Coordinated memory budgets with hard limits give the tightest memory control. The right trade-offs — GC mode, DuckDB/heap budget split, Job Objects for process lifecycle — depend on the application.*
