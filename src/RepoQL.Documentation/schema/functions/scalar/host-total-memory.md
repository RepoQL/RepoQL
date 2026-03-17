---
description: "host_total_memory() → system physical memory bytes"
tags: ["host_total_memory", "memory", "diagnostics", "system"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# host_total_memory

Return the system's total available physical memory in bytes.

## Capsule: HostTotalMemory

**Invariant**
`host_total_memory()` reports total physical memory available to the host runtime.

**Example**
```sql
SELECT host_total_memory() / 1048576 AS system_ram_mb;
```

**Depth**
- Sourced from `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
- Useful for computing working-set pressure relative to machine capacity
