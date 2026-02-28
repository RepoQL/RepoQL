---
description: "Show host memory breakdown across managed, DuckDB, and native pools."
tags: ["command", "memory", "host", "metrics", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::memory

Show a point-in-time host memory breakdown by pool.

---

## Capsule: BasicUsage

**Invariant**
`::memory` reports process working set, managed heap, DuckDB buffer usage, ONNX load state, and UriRegistry estimates.

**Example**
```
::memory
→ Memory Breakdown
  ────────────────
  Process working set:   1,234 MB
    .NET managed heap:     156 MB  (gen0=42 gen1=12 gen2=3)
    DuckDB buffer:         820 MB  (limit: 9,830 MB)
    ONNX model:             25 MB  (loaded)
    Other/native:          233 MB

  UriRegistry:  12,345 files · 247,890 symbols · ~68 MB estimated
  System RAM:   32,768 MB
```

**Depth**
- `Process working set` is total resident process memory.
- `.NET managed heap` is GC-managed memory (`GC.GetTotalMemory(false)`).
- `DuckDB buffer` comes from `duckdb_memory()` and is native DB cache usage.
- `Other/native` is the remainder (runtime/native allocations not counted above).
- `UriRegistry` estimate uses a heuristic from file and symbol counts.

---

## Help

```
::memory --help
→ ::memory — Show host memory breakdown by pool
  Usage: ::memory
```
