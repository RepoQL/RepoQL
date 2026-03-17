---
description: "host_working_set() → process working set bytes"
tags: ["host_working_set", "memory", "diagnostics", "process"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# host_working_set

Return the host process working set in bytes.

## Capsule: HostWorkingSet

**Invariant**
`host_working_set()` returns the OS-reported physical memory currently used by the host process.

**Example**
```sql
SELECT host_working_set() / 1048576 AS working_set_mb;
```

**Depth**
- Equivalent to `Environment.WorkingSet`
- Includes managed heap, DuckDB buffers, ONNX runtime, and other native allocations
