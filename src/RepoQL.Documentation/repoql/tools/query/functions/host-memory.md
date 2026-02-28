---
description: "Host process memory introspection: working set, managed heap, GC stats, and system memory."
tags: ["host_working_set", "host_managed_heap", "host_total_memory", "host_gc_counts", "memory", "diagnostics", "ONNX", "GC"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Host Memory Functions

Query the host process memory state from SQL. These UDFs run host-side and return live runtime values — use them to understand where memory is going.

**Process:** `host_working_set()`, `host_managed_heap()`, `host_total_memory()`
**GC:** `host_gc_counts()`
**DuckDB (built-in):** `duckdb_memory()`, `duckdb_settings()`

---

## Capsule: HostWorkingSet

**Invariant**
`host_working_set()` returns the host process working set (total physical memory used) in bytes.

**Example**
```sql
SELECT host_working_set() / 1048576 AS working_set_mb;
```

**Depth**
- Returns: `BIGINT` — bytes
- This is the OS-reported working set, equivalent to `Environment.WorkingSet`
- Includes all pools: .NET managed heap, DuckDB buffer, ONNX runtime, native allocations

---

## Capsule: HostManagedHeap

**Invariant**
`host_managed_heap()` returns the .NET managed heap size in bytes (approximate, non-compacting).

**Example**
```sql
SELECT host_managed_heap() / 1048576 AS managed_heap_mb;
```

**Depth**
- Returns: `BIGINT` — bytes
- Calls `GC.GetTotalMemory(false)` — does not force a collection
- To estimate native memory (ONNX, tree-sitter, etc.): `host_working_set() - host_managed_heap() - duckdb_total`

---

## Capsule: HostTotalMemory

**Invariant**
`host_total_memory()` returns the total physical memory available on the system in bytes.

**Example**
```sql
SELECT host_total_memory() / 1048576 AS system_ram_mb;
SELECT host_working_set() * 100.0 / host_total_memory() AS pct_used;
```

**Depth**
- Returns: `BIGINT` — bytes
- From `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
- Useful for computing memory pressure relative to system capacity

---

## Capsule: HostGcCounts

**Invariant**
`host_gc_counts()` returns GC generation collection counts as a formatted string.

**Example**
```sql
SELECT host_gc_counts();
-- Returns: "gen0:862803 gen1:1198 gen2:71"
```

**Depth**
- Returns: `VARCHAR` — format: `gen0:N gen1:N gen2:N`
- High gen0 count is normal (short-lived allocations during indexing)
- High gen2 count relative to gen1 suggests large object pressure or fragmentation

---

## Composing a Memory Breakdown

Combine host UDFs with DuckDB introspection for a full picture:

```sql
SELECT
    host_working_set() / 1048576 AS working_set_mb,
    host_managed_heap() / 1048576 AS managed_heap_mb,
    (SELECT COALESCE(SUM(memory_usage_bytes), 0) FROM duckdb_memory()) / 1048576 AS duckdb_mb,
    (host_working_set() - host_managed_heap()
        - (SELECT COALESCE(SUM(memory_usage_bytes), 0) FROM duckdb_memory())) / 1048576 AS native_other_mb,
    host_total_memory() / 1048576 AS system_ram_mb,
    host_gc_counts() AS gc;
```

The `native_other_mb` value captures ONNX runtime, tree-sitter parsers, and other native allocations.
For a formatted summary, use `::memory` instead.
