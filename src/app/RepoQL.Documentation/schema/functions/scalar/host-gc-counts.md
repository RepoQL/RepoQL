---
description: "host_gc_counts() → GC generation collection counts text"
tags: ["host_gc_counts", "gc", "memory", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# host_gc_counts

Return current .NET GC collection counts as formatted text.

## Capsule: HostGcCounts

**Invariant**
`host_gc_counts()` returns GC generation counters in a compact string format.

**Example**
```sql
SELECT host_gc_counts();
```

**Depth**
- Returns `VARCHAR` in the form `gen0:N gen1:N gen2:N`
- High gen0 counts are normal during indexing; unusually high gen2 counts suggest longer-lived pressure or fragmentation
