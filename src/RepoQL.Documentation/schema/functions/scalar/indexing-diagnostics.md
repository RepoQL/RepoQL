---
description: "indexing_diagnostics() → indexer status text"
tags: ["indexing_diagnostics", "indexing", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# indexing_diagnostics

Return a text summary of current indexer state.

## Capsule: IndexingDiagnostics

**Invariant**
`indexing_diagnostics()` returns the host's current indexing status as text.

**Example**
```sql
SELECT indexing_diagnostics();
```

**Depth**
- Best for quick human-readable status checks from SQL
- Prefer `processing_queue()` or `system_health()` when you need structured fields for filtering or joins
