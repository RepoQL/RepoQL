---
description: "Operations(id, kind, scope, state, total_files, indexed_count, embedded_count, failed_count, ready_percent, elapsed_s, created_at, completed_at, fs_files, fs_languages, fs_embed_pct) + processing_queue() + system_health() + failed_files()"
tags: ["Operations", "indexing", "queue", "health", "diagnostics", "progress", "failed", "system_health", "processing_queue"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Operations View

Track indexing progress, queue activity, system health, and failures from SQL.

**Progress tracking:** `Operations` view
**Queue inspection:** `processing_queue()`
**Health overview:** `system_health()`
**Failure debugging:** `failed_files()`

---

## Quick Reference

```sql
-- Current operations
SELECT id, kind, scope, state, ready_percent, elapsed_s FROM Operations;

-- Is anything still running?
SELECT * FROM Operations WHERE state = 'Running';

-- Quick health check
SELECT status, queue_depth, failed_count, host_memory_mb FROM system_health();

-- What failed?
SELECT uri, status, error FROM failed_files();
```

---

## Capsule: OperationsView

**Invariant**
`Operations` view shows all active and completed indexing operations with progress, timing, and filesystem summary.

**Example**
```sql
-- Running imports with progress
SELECT scope, indexed_count, total_files, ready_percent
FROM Operations WHERE state = 'Running';

-- Completed with failures
SELECT scope, failed_count, elapsed_s
FROM Operations WHERE state = 'CompletedWithFailures';

-- Import with filesystem stats
SELECT scope, fs_files, fs_languages, fs_embed_pct
FROM Operations WHERE kind = 'import';
```
//BOUNDARY: In-memory only; resets on host restart. Import metadata joined from `Filesystems`.

**Depth**
- `kind`: Operation type — `import` (github:// repos), `startup` (initial indexing), `reindex`
- `scope`: Target URI or path (e.g., `github://dotnet/aspire`, `C:\Source\RepoQL`)
- `state`: `Running`, `Completed`, `CompletedWithFailures`, `Cancelled`
- `ready_percent`: 0–100, percentage of files that reached terminal state
- `elapsed_s`: Seconds since creation (live for running, final for completed)
- `fs_files`, `fs_languages`, `fs_embed_pct`: Joined from `Filesystems` view for imports (NULL for non-import operations)

---

## Capsule: ProcessingQueue

**Invariant**
`processing_queue()` returns currently queued and in-flight indexing items with age tracking.

**Example**
```sql
-- What's being processed right now?
SELECT uri, stage, status, age_seconds FROM processing_queue();

-- Stuck items (queued for over 60 seconds)
SELECT uri, stage, age_seconds FROM processing_queue()
WHERE age_seconds > 60 ORDER BY age_seconds DESC;

-- Queue depth by stage
SELECT stage, COUNT(*) as items, MAX(age_seconds) as oldest
FROM processing_queue() GROUP BY stage;
```
//BOUNDARY: Returns empty when queue is idle. This is a live snapshot — results change between calls.

**Depth**
- `uri`: File being processed
- `stage`: Queue or operation name — queues: `HotPath`, `Analysis`, `IdleProcessing`, `DeferredRetry`. Operations: `classification`, `parsing`, `single_file_analysis`, `analysis`, `idle_retry_analysis`
- `status`: Item status — `queued`, `processing`, `deferred`, `retrying`
- `age_seconds`: Seconds since the item was enqueued
- `size_bytes`: File size
- `mime_type`: Detected media type (may be NULL for unclassified items)

---

## Capsule: SystemHealth

**Invariant**
`system_health()` returns a single-row summary of host status, queue depth, resource usage, and the last error.

**Example**
```sql
-- Quick health check
SELECT * FROM system_health();

-- Monitor resource usage
SELECT host_memory_mb, db_size_mb, disk_free_mb FROM system_health();

-- Check for problems
SELECT status, failed_count, last_error FROM system_health()
WHERE failed_count > 0 OR status = 'error';
```
//BOUNDARY: Single row, always returns. Returns status='error' if diagnostics provider is unavailable.

**Depth**
- `status`: `idle` (all queues drained), `indexing` (hot-path active), `idle_processing` (embeddings, pruning, deferred retries), `analyzing` (multi-file analysis), `error` (provider unavailable)
- `queue_depth` and `active_workers`: hot-path + analysis + idle queue counts (excludes deferred retry items)
- `last_error`: Most recent error message — useful for diagnosing stuck pipelines

---

## Capsule: FailedFiles

**Invariant**
`failed_files(pattern)` returns files that failed indexing, were skipped, or had embedding failures, with their error messages.

**Example**
```sql
-- All failures (pattern is optional)
SELECT uri, status, error FROM failed_files();

-- Scoped to a directory
SELECT uri, error FROM failed_files('src/**');

-- Failures in an imported repo
SELECT uri, error FROM failed_files('github://dotnet/aspire/**');

-- Count failures by error pattern
SELECT
    CASE WHEN error LIKE '%timeout%' THEN 'timeout'
         WHEN error LIKE '%transaction%' THEN 'transaction'
         ELSE 'other' END as category,
    COUNT(*)
FROM failed_files() GROUP BY 1;

-- Filter by failure type
SELECT uri, error FROM failed_files() WHERE status = 'Failed';
SELECT uri, error FROM failed_files() WHERE status = 'Skipped';
```
//BOUNDARY: Pattern is optional (NULL = all files). Includes indexing failures, skipped files, and embedding failures.

**Depth**
- `pattern`: Optional glob pattern to scope results (NULL for all files, `'src/**/*.cs'` for C# files)
- `uri`: File URI
- `status`: Why this file appears — `Failed` (pipeline error), `Skipped` (intentionally skipped), or `Indexed` (indexing succeeded but embedding failed)
- `error`: Error message from the pipeline (may be NULL for skipped files)

---

## Common Patterns

| Goal | Query |
|------|-------|
| All operations | `SELECT * FROM Operations` |
| Running operations | `SELECT * FROM Operations WHERE state = 'Running'` |
| Import progress | `SELECT scope, ready_percent FROM Operations WHERE kind = 'import'` |
| Queue depth | `SELECT queue_depth FROM system_health()` |
| Is host idle? | `SELECT status FROM system_health()` |
| What's processing? | `SELECT uri, stage FROM processing_queue()` |
| Stuck items | `SELECT uri, age_seconds FROM processing_queue() WHERE age_seconds > 60` |
| All failures | `SELECT uri, status, error FROM failed_files()` |
| Memory pressure | `SELECT host_memory_mb, db_size_mb, disk_free_mb FROM system_health()` |
| Wait for completion | Poll `SELECT ready_percent FROM Operations WHERE id = '...'` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Expecting operations to persist across restarts | Operations are in-memory; use `Filesystems` view for durable import state |
| Using `processing_queue()` to check if indexing is done | Use `system_health()` status or `Operations` ready_percent instead |
| Expecting only `Failed` status | `failed_files()` also returns `Skipped` and `Indexed` (embedding-failed) entries — filter with `WHERE status = 'Failed'` if needed |
| Confusing `failed_count` in Operations vs system_health | Operations tracks per-operation failures; system_health shows total across all |

---

## Column Reference

### Operations View

| Column | Type | Description |
|--------|------|-------------|
| `id` | uuid | Operation identifier |
| `kind` | string | Operation type: `import`, `startup`, `reindex` |
| `scope` | string | Target URI or path |
| `state` | string | `Running`, `Completed`, `CompletedWithFailures`, `Cancelled` |
| `total_files` | integer | Total files in scope |
| `indexed_count` | integer | Files that completed indexing |
| `embedded_count` | integer | Files that completed embedding |
| `failed_count` | integer | Files that failed |
| `ready_percent` | integer | Completion percentage (0–100) |
| `elapsed_s` | float | Seconds elapsed (live or final) |
| `created_at` | timestamp | When the operation started |
| `completed_at` | timestamp | When it finished (NULL if running) |
| `fs_files` | integer | File count from Filesystems (imports only) |
| `fs_languages` | string | Comma-separated languages (imports only) |
| `fs_embed_pct` | integer | Embedding completion % (imports only) |

### processing_queue()

| Column | Type | Description |
|--------|------|-------------|
| `uri` | string | File being processed |
| `stage` | string | Queue or operation name |
| `status` | string | `queued`, `processing`, `deferred`, or `retrying` |
| `age_seconds` | integer | Seconds since enqueued |
| `size_bytes` | bigint | File size |
| `mime_type` | string | Detected media type (nullable) |

### system_health()

| Column | Type | Description |
|--------|------|-------------|
| `status` | string | `idle`, `indexing`, `idle_processing`, `analyzing`, or `error` |
| `queue_depth` | integer | Total queued items |
| `active_workers` | integer | Currently processing |
| `failed_count` | integer | Total failed files |
| `stale_count` | integer | Files needing re-index |
| `epoch` | bigint | Indexing batch counter |
| `last_error` | string | Most recent error (nullable) |
| `host_memory_mb` | integer | Host working set in MB |
| `db_size_mb` | integer | Database file size in MB |
| `disk_free_mb` | integer | Available disk space in MB |

### failed_files(pattern)

| Column | Type | Description |
|--------|------|-------------|
| `uri` | string | File URI |
| `status` | string | `Failed`, `Skipped`, or `Indexed` (embedding failure) |
| `error` | string | Error message (nullable) |
