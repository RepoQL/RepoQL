---
description: "system_health() → status, queue_depth, failed_count, host_memory_mb, db_size_mb"
tags: ["system_health", "operations", "diagnostics", "health"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# system_health

Return a single-row health summary for the host and indexer.

## Capsule: SystemHealth

**Invariant**
`system_health()` always returns one summary row for queue state, failure counts, and resource usage.

**Example**
```sql
SELECT status, failed_count, last_error
FROM system_health()
WHERE failed_count > 0 OR status = 'error';
```
//BOUNDARY: Single row, always returns. Returns status='error' if diagnostics provider is unavailable.

**Depth**
- `status` distinguishes `idle`, `indexing`, `idle_processing`, `analyzing`, and `error`
- `queue_depth` and `active_workers` exclude deferred retry items, so use `processing_queue()` for exact live contents
