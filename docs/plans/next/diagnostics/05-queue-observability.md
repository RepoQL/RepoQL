---
description: Plan for queue observability UDFs — processing_queue(), failed_files(), system_health(), CreatedAt timestamp
tags: [diagnostics, queue, observability, udf, sql, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Queue Observability UDFs

Implements: [Runtime Observability Design](../../../designs/future/runtime-observability.md) — processing_queue(), failed_files(), system_health(), CreatedAt on IndexItem

## Scope

**Covers:**
- `CreatedAt` (DateTimeOffset) field on `IndexItem`, set during classification
- `EnqueuedAt` field on `QueuedItemInfo`, populated from `IndexItem.CreatedAt`
- `processing_queue()` structured UDF — per-item queue visibility with timing
- `failed_files()` SQL macro over `_indexer_errors_internal()`
- `system_health()` structured UDF — single-row summary combining pipeline snapshot, UriRegistry counts, and process metrics
- Tests for each UDF and the new timestamp field

**Does not cover:**
- TrustSignal / enhanced footer (Plan: [04-trust-signal](04-trust-signal.md))
- Queue manipulation commands (Plan: [06-queue-commands](06-queue-commands.md))
- Per-item cancellation in WorkQueue (Plan: [06-queue-commands](06-queue-commands.md))
- Failure history (attempt count, timestamps) — design explicitly deferred this

## Enables

- Agent sees what the pipeline is doing right now — which files are queued, which are stuck
- Agent identifies stuck files by age: `SELECT * FROM processing_queue() WHERE age_seconds > 60`
- Agent gets a single-row health summary without running `::diagnostics`
- Plan 06 can use `processing_queue()` to verify cancel/skip/retry worked
- North-star satisfied: "An agent should be able to see what the system is doing right now"

## Prerequisites

- Plan: [04-trust-signal](04-trust-signal.md) — cached `GetSummary()` is used by `system_health()`. If Plan 04 is not complete, `system_health()` can compute counts directly (less efficient but functional).

## North Star

The SQL surface answers: what's happening, what's stuck, what failed, how are resources. No commands, no special access — just SQL. The agent's first instinct (`SELECT * FROM processing_queue()`) works.

## Done Criteria

### CreatedAt on IndexItem

- `IndexItem` shall gain a `CreatedAt` property (DateTimeOffset), set when the item is first created during classification
- `QueuedItemInfo` shall gain an `EnqueuedAt` property (DateTimeOffset), populated from `IndexItem.CreatedAt`
- `IndexingEngineDiagnosticsProvider.GetQueuedItems()` shall populate `EnqueuedAt` on each returned item
- A test shall verify `CreatedAt` is set during classification and propagated to `QueuedItemInfo.EnqueuedAt`

### processing_queue() UDF

- A structured UDF named `processing_queue` shall be registered following the `[StructuredUdf]` pattern in `UdfImplementations/`
- The UDF shall call `IIndexingDiagnosticsProvider.GetQueuedItems()` and return one row per item
- Columns:

| Column | Type | Source |
|--------|------|--------|
| `uri` | VARCHAR | `QueuedItemInfo.Uri` |
| `stage` | VARCHAR | `HotPath`, `Analysis`, `IdleProcessing` |
| `status` | VARCHAR | `queued`, `processing` |
| `age_seconds` | INTEGER | `(DateTimeOffset.UtcNow - EnqueuedAt).TotalSeconds`, rounded down |
| `size_bytes` | BIGINT | `QueuedItemInfo.Size` |
| `mime_type` | VARCHAR | `QueuedItemInfo.MimeType` |

- When the queue is empty, return zero rows (not an error)
- A test shall verify the UDF returns items during active indexing
- A test shall verify `age_seconds` is computed correctly
- A test shall verify empty queue returns zero rows

### failed_files() Macro

- A SQL macro named `failed_files` shall be registered as:
  ```sql
  CREATE MACRO failed_files() AS TABLE
    SELECT uri, status, error FROM _indexer_errors_internal();
  ```
- Zero code change — the macro wraps the existing `_indexer_errors_internal()` UDF
- The macro shall be registered during UDF initialization alongside existing macros
- A test shall verify the macro returns files with `Failed` status
- A test shall verify the macro returns files with `EmbeddingStatus == Failed`

### system_health() UDF

- A structured UDF named `system_health` shall be registered following the `[StructuredUdf]` pattern
- The UDF shall return a single row combining:

| Column | Type | Source |
|--------|------|--------|
| `status` | VARCHAR | `idle`, `indexing`, `analyzing`, `idle_processing` — from `GetSnapshot().Status` |
| `queue_depth` | INTEGER | Sum of all queue depths from snapshot |
| `active_workers` | INTEGER | Sum of active workers from snapshot |
| `failed_count` | INTEGER | Failed file count from UriRegistry (via cached summary if available, or direct count) |
| `stale_count` | INTEGER | Stale file count from UriRegistry |
| `epoch` | BIGINT | Current epoch from snapshot |
| `last_error` | VARCHAR | Most recent error from snapshot |
| `host_memory_mb` | INTEGER | `Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)` |
| `db_size_mb` | INTEGER | File size of `index.duckdb` / (1024 * 1024) |
| `disk_free_mb` | INTEGER | `DriveInfo.AvailableFreeSpace` on `.repoql/` volume / (1024 * 1024) |

- `host_memory_mb` shall read `Process.GetCurrentProcess().WorkingSet64` directly — not from OpenTelemetry gauges (UDFs can't read OTel values)
- `db_size_mb` and `disk_free_mb` are file system calls — no caching needed
- A test shall verify the UDF returns a single row with all columns populated
- A test shall verify `host_memory_mb` is a reasonable value (> 0)

## Constraints

- **No new tables** — schema is frozen. All UDFs return virtual tables computed in-memory. Design constraint.
- **`[StructuredUdf]` pattern** — follow existing UDF conventions in `UdfImplementations/`. Auto-discovered via attributes.
- **In-memory data only** — `processing_queue()` and `system_health()` read from `IIndexingDiagnosticsProvider` and `UriRegistry`, both in-memory. No DuckDB queries to serve diagnostic queries.
- **`failed_files()` is a macro, not a UDF** — design chose zero code change over a new UDF. The macro wraps existing `_indexer_errors_internal()`.
- **Point-in-time snapshots** — `processing_queue()` is a snapshot of the queue at call time. Items may have moved between the query and the agent reading results. This is inherent and documented, not a bug.

## References

- [Runtime Observability Design](../../../designs/future/runtime-observability.md) — processing_queue(), failed_files(), system_health() sections
- [Queue Observability Flow](../../../flows/future/diagnostics/queue-observability.md) — SQL query patterns, diagnosis logic
- `src/RepoQL.Data.DuckDB/UdfImplementations/UriRegistryUdf.cs` — existing UDF pattern, `_indexer_errors_internal()`
- `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngineDiagnosticsProvider.cs` — `GetQueuedItems()`, `GetSnapshot()`
- `src/RepoQL.Contracts/Diagnostics/IIndexingDiagnosticsProvider.cs` — interface contract
- `src/RepoQL.Core/WorkQueue.cs` — queue infrastructure
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions

## Error Policy

UDFs must never throw to the SQL caller. If `GetQueuedItems()` or `GetSnapshot()` fails:
1. Log the error
2. Return an empty result set for `processing_queue()`
3. Return a single row with nulls/zeros for `system_health()` and `status = 'error'`

This follows the existing UDF error handling pattern — the SQL surface must remain stable even when the host internals are unhealthy.
