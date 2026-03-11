---
description: "Show the top managed heap types in the host process. Expensive and managed-only."
tags: ["command", "memory", "heap", "diagnostics", "gc"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::diagnostics.memory.heap

Show the top managed object types in the host process by shallow size.

---

## Capsule: BasicUsage

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

**Depth**
- The command is best-effort and may fail on platforms or runtimes that do not allow live heap inspection.
- The output is managed heap only. Large native allocations will not appear here.
- Heap labels (`Gen2`, `LOH`, `POH`) show where the type's bytes are concentrated, not a complete lifetime story.

---

## Help

```
::diagnostics.memory.heap --help
→ ::diagnostics.memory.heap — Show top managed heap types in the host (expensive)
  Usage: ::diagnostics.memory.heap
```
