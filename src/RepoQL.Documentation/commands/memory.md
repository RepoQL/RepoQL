---
description: "::diagnostics.memory (pool breakdown: managed/DuckDB/native) and ::diagnostics.memory.heap (top managed types, expensive) — host memory inspection"
tags: ["command", "memory", "host", "metrics", "diagnostics", "heap", "gc"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# Memory Commands

Inspect host process memory usage.

**Pool breakdown:** `::diagnostics.memory` — fast overview by pool
**Heap drilldown:** `::diagnostics.memory.heap` — top managed types (expensive)

---

## ::diagnostics.memory

### Capsule: BasicUsage

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
- `Host working set` is total resident process memory
- `.NET managed heap` is GC-managed memory (`GC.GetTotalMemory(false)`)
- `DuckDB buffer` comes from `duckdb_memory()` — native DB cache usage
- `Other/native` is the remainder (runtime/native allocations not counted above)
- Use `::diagnostics.memory.heap` when you need the top managed object types rather than pool totals

---

## ::diagnostics.memory.heap

### Capsule: BasicUsage

**Invariant**
`::diagnostics.memory.heap` attaches to the live host process, walks the managed heap, and reports the largest managed types by shallow bytes. It is slower and heavier than `::diagnostics.memory`.

**Example**
```
::diagnostics.memory.heap
→ Managed Heap
  ────────────
  Host PID:             4,242
  Total objects:    1,280,552
  Total shallow size:   612 MB
  Top managed types by shallow size:
    System.Byte[]                 98,240 objs     344 MB   [LOH 96%, Gen2 4%]
    System.String                412,118 objs      88 MB   [Gen2 100%]
    System.Char[]                 36,004 objs      52 MB   [LOH 72%, Gen2 28%]

  Notes:
    - shallow managed bytes only; retained size is not computed
    - native allocations (DuckDB, ONNX, runtime) are excluded
    - use ::diagnostics.memory for full process memory pools
```
//BOUNDARY: Managed heap only. Native allocations (DuckDB, ONNX, runtime) are excluded. May fail on platforms that don't support live heap inspection.

**Depth**
- Heap labels (`Gen2`, `LOH`, `POH`) show where the type's bytes are concentrated
- Best-effort — may fail on some platforms/runtimes

---

## Help

```
::diagnostics.memory --help
::diagnostics.memory.heap --help
```
