---
description: "host_process_memory() → process memory metrics as JSON"
tags: ["host_process_memory", "memory", "diagnostics", "process"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# host_process_memory

Return process-level memory metrics as JSON.

## Capsule: HostProcessMemory

**Invariant**
`host_process_memory()` returns the host process memory counters as a JSON object.

**Example**
```sql
SELECT host_process_memory();
```

**Depth**
- JSON includes `working_set_bytes`, `peak_working_set_bytes`, `private_memory_bytes`, `paged_memory_bytes`, and `virtual_memory_bytes`
- Use it when you need more than the single-number view from `host_working_set()`
