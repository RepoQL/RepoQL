---
description: "host_gc_memory_info() → GC memory details as JSON"
tags: ["host_gc_memory_info", "gc", "memory", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# host_gc_memory_info

Return detailed .NET GC memory information as JSON.

## Capsule: HostGcMemoryInfo

**Invariant**
`host_gc_memory_info()` exposes the runtime GC memory snapshot as a JSON object.

**Example**
```sql
SELECT host_gc_memory_info();
```

**Depth**
- JSON includes `heap_size_bytes`, `fragmented_bytes`, `committed_bytes`, `memory_load_bytes`, `high_memory_load_threshold_bytes`, `total_available_memory_bytes`, `finalization_pending_count`, and `pause_time_percentage`
- Use it when `host_gc_counts()` is too coarse and you need fragmentation or load-threshold detail
