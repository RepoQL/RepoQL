---
description: "host_managed_heap() → managed heap bytes"
tags: ["host_managed_heap", "gc", "memory", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# host_managed_heap

Return the .NET managed heap size in bytes.

## Capsule: HostManagedHeap

**Invariant**
`host_managed_heap()` reports the current managed heap size without forcing a collection.

**Example**
```sql
SELECT host_managed_heap() / 1048576 AS managed_heap_mb;
```

**Depth**
- Calls `GC.GetTotalMemory(false)`, so the value is approximate and non-compacting
- Compare it with `host_working_set()` and DuckDB memory to estimate native allocations
