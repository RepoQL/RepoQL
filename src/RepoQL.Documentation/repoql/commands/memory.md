---
description: "Show host memory breakdown across managed, DuckDB, and native pools, with a separate expensive heap-type drilldown."
tags: ["command", "memory", "host", "metrics", "diagnostics", "heap"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::diagnostics.memory

Show a point-in-time host memory breakdown by pool.

---

## Capsule: BasicUsage

**Invariant**
`::diagnostics.memory` reports process working set, managed heap, DuckDB buffer usage, ONNX load state, and UriRegistry estimates.

**Example**
```
::diagnostics.memory
→ Memory
  ────────────────
  Host working set:      1,234 MB   (system: 32,768 MB, 4% used)
    Peak working set:    1,260 MB
    Private bytes:       1,198 MB
    .NET live heap:        156 MB   (gen0:42 gen1:12 gen2:3)
      GC committed:        192 MB
      GC fragmented:        12 MB
    DuckDB buffer:         820 MB   (limit: 9,830 MB)
    Native other:          258 MB

  Files:              12,345
  Symbols:           247,890
```

**Depth**
- `Host working set` is total resident process memory.
- `.NET managed heap` is GC-managed memory (`GC.GetTotalMemory(false)`).
- `DuckDB buffer` comes from `duckdb_memory()` and is native DB cache usage.
- `Other/native` is the remainder (runtime/native allocations not counted above).
- `Peak`, `private`, `virtual`, and GC committed/fragmented values come from host-side process and GC snapshots.
- Use `::diagnostics.memory.heap` when you need the top managed object types rather than pool totals.

---

## Help

```
::diagnostics.memory --help
→ ::diagnostics.memory — Show host memory breakdown by pool
  Usage: ::diagnostics.memory
```
