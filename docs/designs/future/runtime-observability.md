# Runtime Observability Design

## North Star

See what the system is doing right now. Trust has layers — structural readiness, semantic readiness, failures, and freshness — each visible at the depth you need, from 10 tokens on every response to a full SQL query.

## Context

Two capabilities share the same data source: the enhanced footer (trust signals on every tool response) and queue observability (SQL surface for what the pipeline is doing right now). Both surface UriRegistry state — the footer at a glance, SQL UDFs at any depth.

Today, the data exists but the surfaces are narrow:
- `IndexerStatus` carries 4 fields (pending, semantic ready, semantic enabled, elapsed). No failure count, no stale count, no percentages.
- `UriRegistryUdf` has `_indexer_errors_internal`, `_registry_summary_internal`, `_scope_readiness_internal` — but no processing queue visibility, no system health summary.
- `IndexingEngineDiagnosticsProvider` has `GetQueuedItems()` and `GetSnapshot()` — but they're not exposed as SQL.
- `WorkQueue<T>` has no per-item timing.

The gap: the agent sees "ready" or "N pending." It can't see "3 failed," "12 stale," "87% indexed," or "file X stuck for 97 seconds."

**Enables:**
- [Footer Trust Signals Flow (current)](../../flows/current/mcp/footer-trust-signals.md)
- [Footer Trust Signals Flow (enhanced)](../../flows/future/diagnostics/footer-trust-signals.md)
- [Queue Observability Flow](../../flows/future/diagnostics/queue-observability.md)
- [Self-Service Troubleshooting Meta-Flow](../../flows/future/diagnostics/self-service-troubleshooting.md) — stages 1-2 (detection + triage)

**Built on:**
- `UriRegistry` — in-memory source of truth for file status
- `ScopeReadiness` — already computes percentages and counts
- `IndexingEngineDiagnosticsProvider` — already has per-item queue visibility
- `UriRegistryUdf` — existing UDF infrastructure for SQL exposure

## Constraints

- **Footer under 20 tokens** (common cases) — the north-star budget is a contract
- **No new tables** — schema is frozen. Extend via views, macros, UDFs
- **In-memory computation only** — UriRegistry and diagnostics provider are in-memory; no additional DB queries for footer data
- **Footer on every response** — cost must be negligible. `UriRegistry.GetSummary()` is O(n) today; a cached summary with mutation-based invalidation is required, not optional.
- **Proto compatibility** — new fields on gRPC response messages; existing clients ignore them

---

## Components

```
┌─────────────────────────────────────────────────────────────────┐
│                        Host Process                               │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │               UriRegistry (in-memory)                      │   │
│  │  FileEntry: Status, Error, IndexedAt, EmbeddingStatus     │   │
│  └──────────────┬──────────────────────┬─────────────────────┘   │
│                 │                       │                          │
│        ┌────────▼──────────┐   ┌───────▼───────────────────┐    │
│        │  TrustSignal      │   │  UDF Layer                  │    │
│        │  (per response)   │   │                              │    │
│        └────────┬──────────┘   │  processing_queue()          │    │
│                 │               │  failed_files()  [macro]     │    │
│        ┌────────▼──────────┐   │  system_health()              │    │
│        │  gRPC Response    │   │  indexer_status() [existing]  │    │
│        │  (proto fields)   │   └───────┬───────────────────┘    │
│        └────────┬──────────┘           │                          │
│                 │               ┌──────▼───────────────────┐    │
│                 │               │  IndexingEngine Diag.      │    │
│                 │               │  GetQueuedItems()           │    │
│                 │               │  GetSnapshot()              │    │
│                 │               └────────────────────────────┘    │
└─────────────────┼────────────────────────────────────────────────┘
                  │
       ┌──────────▼──────────────────────────────────────┐
       │              MCP Client Process                     │
       │                                                     │
       │  gRPC response → TrustSignal → FormatStatusFooter  │
       │                                                     │
       │  [1.5k tok | 42ms | index: ready | semantic: ready] │
       └─────────────────────────────────────────────────────┘
```

---

## Design

### Trust Signal (Enhanced Footer)

Replace `IndexerStatus` (4 fields) with `TrustSignal` that carries all four trust dimensions:

```
TrustSignal:
  index_total        int     — total discovered files
  index_pending      int     — files not yet indexed
  index_failed       int     — files in Failed status
  index_stale        int     — files modified since indexing
  semantic_enabled   bool    — embeddings feature on?
  semantic_ready     bool    — all embeddings complete?
  semantic_percent   int     — percentage of applicable files embedded
  execution_time_ms  long    — query execution time
```

`index_total` enables computing percentages on the client: `(total - pending) / total * 100`. This avoids floating-point in the proto and keeps the computation on the formatting side.

**Computation:** The host computes TrustSignal from `UriRegistry.GetSummary()` — a method that walks the registry once and returns counts by status. Today this is O(n) per call. Since the footer is computed on every tool response, a cached summary is required: cache the result on first call, invalidate when UriRegistry mutations occur (status changes, new registrations). Batch invalidation (dirty flag, recompute on next read) prevents invalidation storms during rapid indexing.

**Proto changes:** Add fields to `QueryResponse` and `ExploreIndexerStatus`:

```protobuf
int32 index_total = 10;
int32 index_failed = 11;
int32 index_stale = 12;
int32 semantic_percent = 13;
```

Existing fields (`index_pending`, `semantic_enabled`, `semantic_ready`, `execution_time_ms`) unchanged. Old clients ignore new fields. New clients with old hosts see zeros — footer degrades to current behavior.

### Footer Formatting

`FormatStatusFooter` evolves to show degraded dimensions while keeping healthy signals compact.

**Formatting rules:**
1. **Healthy signals compress:** `index: ready` (not `index: 100% (0 pending)`)
2. **Degraded signals expand:** `index: 94% (47 pending)` — percentage + count
3. **Failure/stale signals appear only when non-zero:** `3 failed`, `stale: 12`
4. **NOT READY breaks the compact format** when discovery hasn't completed

**Token budget verification:**

| Case | Footer | Tokens |
|------|--------|--------|
| Healthy | `[1.5k tok \| 42ms \| index: ready \| semantic: ready]` | ~14 |
| Partial | `[850 tok \| 120ms \| index: 94% (47 pending) \| semantic: 72%]` | ~18 |
| Failures | `[1.2k tok \| 35ms \| index: ready \| semantic: ready \| 3 failed]` | ~16 |
| Worst case | `[850 tok \| 120ms \| index: 87% (102 pending) \| semantic: 72% \| 5 failed \| stale: 3]` | ~24 |

Common cases stay under 20. Worst case (all dimensions degraded simultaneously) costs ~24 — acceptable as a trade against requiring a separate diagnostic query.

### SQL Surface: processing_queue()

New structured UDF wrapping `IndexingEngineDiagnosticsProvider.GetQueuedItems()`:

```sql
SELECT uri, stage, status, age_seconds, size_bytes, mime_type
FROM processing_queue()
ORDER BY age_seconds DESC;
```

| Column | Type | Source |
|--------|------|--------|
| `uri` | VARCHAR | `QueuedItemInfo.Uri` |
| `stage` | VARCHAR | HotPath, Analysis, IdleProcessing |
| `status` | VARCHAR | queued, processing |
| `age_seconds` | INTEGER | Now - `IndexItem.CreatedAt` |
| `size_bytes` | BIGINT | `QueuedItemInfo.Size` |
| `mime_type` | VARCHAR | `QueuedItemInfo.MimeType` |

**Requires:** Add `CreatedAt` (DateTimeOffset) to `IndexItem`, set when the item is first created during classification. The diagnostics provider passes it through to `QueuedItemInfo` as `EnqueuedAt`. This gives total pipeline age, which is more useful to agents than per-stage time.

### SQL Surface: failed_files()

Already nearly exists as `_indexer_errors_internal()`. A SQL macro adds discoverability without code:

```sql
CREATE MACRO failed_files() AS TABLE
  SELECT uri, status, error FROM _indexer_errors_internal();
```

Zero code change. The `_indexer_errors_internal()` UDF already returns URI, status, and error for files with `Status == Failed` or `EmbeddingStatus == Failed`.

If failure history becomes needed (attempt count, timestamps), upgrade to a full UDF later.

### SQL Surface: system_health()

Single-row summary combining `IndexingEngineDiagnosticsProvider.GetSnapshot()` + `UriRegistry.GetSummary()` + process metrics:

```sql
SELECT * FROM system_health();
```

| Column | Type | Source |
|--------|------|--------|
| `status` | VARCHAR | idle, indexing, analyzing, idle_processing |
| `queue_depth` | INTEGER | Sum of all queue depths from snapshot |
| `active_workers` | INTEGER | Sum of active workers from snapshot |
| `failed_count` | INTEGER | UriRegistry failed count |
| `stale_count` | INTEGER | UriRegistry stale count |
| `epoch` | BIGINT | Current epoch from snapshot |
| `last_error` | VARCHAR | Most recent error from snapshot |
| `host_memory_mb` | INTEGER | `Process.GetCurrentProcess().WorkingSet64 / 1MB` |
| `db_size_mb` | INTEGER | File size of `index.duckdb` / 1MB |
| `disk_free_mb` | INTEGER | `DriveInfo.AvailableFreeSpace` on `.repoql/` volume / 1MB |

`host_memory_mb` reads `Process.GetCurrentProcess().WorkingSet64` directly (the same computation `HostMetrics` uses for its OpenTelemetry gauge, but called inline since UDFs can't read OTel gauge values). `db_size_mb` and `disk_free_mb` are file system calls — cheap, no caching needed.

### Queue Commands

Three commands for queue manipulation. These are Control (from the north-star), not Observability, but they're coupled to the queue infrastructure designed here.

#### `::queue.cancel[uri]`

Remove an item from processing and mark it Failed.

Two approaches considered:

1. **CancellationToken per item in WorkQueue<T>:** Each item gets a CTS. Cancel signals the worker to abandon it. Complex — requires plumbing cancellation through the processing pipeline.

2. **Fail-fast via UriRegistry status:** Mark the URI as `Failed` in UriRegistry. The processing pipeline checks URI status at stage boundaries. When the next stage sees `Failed`, it skips the item. The item remains in WorkQueue until the worker naturally completes, but no further stages run.

**Decision: Option 2.** Simpler, doesn't require WorkQueue changes, works across all pipeline stages. The trade-off: a stuck parser won't be interrupted mid-stage. But the existing processing timeout mechanism handles true infinite loops, and slow files (the common case) finish their current stage then stop.

**New work required:** The processing pipeline does not currently check URI status at stage boundaries. Status checks must be added at each transition point (classification → parsing → analysis → commit). This is straightforward — a guard clause before each stage — but touches the hot path and must not degrade throughput. The check is a dictionary lookup on `UriRegistry[uri].Status`, which is O(1).

#### `::queue.skip[uri]`

Permanently exclude a file across restarts.

Mechanism: Set a `Skipped` flag on the `FileEntry` in UriRegistry. The indexing pipeline checks this flag when considering files for enqueue. On host restart, file discovery skips entries in the skip list.

**Persistence:** UriRegistry is in-memory, rebuilt on restart. Skip state persists via `.repoql/skip-list.txt` — a simple, human-readable, editable file. The indexing engine reads it on startup and during file discovery. Agents and humans can inspect and edit it directly.

#### `::queue.retry[uri]`

Reset a failed file's status to `Discovered` for reprocessing. If the file is in the skip list, remove it. The next processing cycle picks it up.

### Cross-Cutting Concerns

**Footer and UDFs share a computation path.** The footer's trust signal and `system_health()` UDF both need UriRegistry summary + indexing snapshot. Both call `UriRegistry.GetSummary()`, which returns the cached summary (dirty-flag invalidation ensures the cache is fresh without O(n) per call). No separate data path needed.

**Existing UDFs remain.** `_indexer_status_internal()`, `_registry_summary_internal()`, `_scope_readiness_internal()` continue to work. The new UDFs are additional entry points, not replacements.

**Proto evolution is backward-compatible.** New fields are additive. Old clients ignore them. New clients with old hosts get default values (0, false) which naturally produce the current footer format.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Extend IndexerStatus → TrustSignal | New separate trust signal type | Minimal change surface; existing callers adapt naturally |
| SQL macro for failed_files() | New dedicated UDF | Zero code; `_indexer_errors_internal()` already exists |
| UriRegistry status change for cancel | Per-item CancellationToken in WorkQueue | Much simpler; works across all stages; no WorkQueue changes |
| Skip list file | DuckDB-stored skip state | Human-readable, editable, no schema change needed |
| `index_total` in proto (compute % client-side) | `index_percent` in proto (compute on host) | Avoids float, client decides formatting, total is independently useful |
| `CreatedAt` on IndexItem | `EnqueuedAt` per WorkQueue stage | Total pipeline age is more useful than per-stage time |

## Alternatives Considered

**Dedicated diagnostics gRPC service:** Separate service for all diagnostic queries. Rejected — adds deployment complexity, doesn't leverage the SQL surface. UDFs are more composable (agents can join diagnostics with code data).

**WebSocket push for queue updates:** Real-time queue state pushed to clients. Rejected — the `WatchStatus` streaming RPC already exists for the dashboard. SQL queries are better for agent investigation (agents pull when needed).

**Failure history in UriRegistry:** Track per-file attempt count, historical errors, timestamps of each failure. Rejected for now — adds memory pressure for a feature that rarely matters. Single-error tracking is sufficient. Attempt count can be added later if retry patterns need detection.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Footer exceeds 20 tokens too often | Formatting rules compress healthy dimensions; only degraded signals expand. Verify with real repos. |
| `UriRegistry.GetSummary()` too slow for large repos | Cache summary, invalidate on mutations. Batch invalidation so rapid mutations don't trigger recomputation storm. |
| `processing_queue()` returns stale data | Items are in-memory; read is a point-in-time snapshot. Label as such. |
| Skip list file conflicts between concurrent hosts | Single-writer (one host per repo, enforced by HostLock). Read on startup, written by commands. |
| Queue cancel doesn't interrupt stuck parser | Document this limitation. Processing timeouts handle true infinite loops. Restart remains the fallback for a truly stuck host. |

## Extension Points

- **New trust dimensions** — add fields to TrustSignal and formatting rules without changing the proto contract
- **New UDFs** — follow existing `[StructuredUdf]` pattern in `UdfImplementations/`
- **New queue commands** — follow existing `[Command]` pattern in `CommandImplementations/`
- **`system_health()` columns** — add columns without breaking existing queries
- **Alerting thresholds** — `system_health()` data could feed configurable alerts (future)

## Related

- North star: `docs/north-star/diagnostics.md` (Trust, Observability, Control sections)
- Flow — current footer: `docs/flows/current/mcp/footer-trust-signals.md`
- Flow — enhanced footer: `docs/flows/future/diagnostics/footer-trust-signals.md`
- Flow — queue observability: `docs/flows/future/diagnostics/queue-observability.md`
- Implementation — footer: `src/RepoQL.Explore/RepresentationFormatter.cs`, `src/RepoQL.Explore/IndexerStatus.cs`
- Implementation — UDFs: `src/RepoQL.Data.DuckDB/UdfImplementations/UriRegistryUdf.cs`
- Implementation — diagnostics provider: `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngineDiagnosticsProvider.cs`
- Implementation — work queue: `src/RepoQL.Core/WorkQueue.cs`
- Implementation — proto: `src/RepoQL.Protocol/Protos/repoql.proto`
