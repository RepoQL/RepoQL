---
description: "indexing_queue() → pending items as JSON"
tags: ["indexing_queue", "indexing", "queue", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# indexing_queue

Return pending indexing items as JSON.

## Capsule: IndexingQueue

**Invariant**
`indexing_queue()` returns the pending indexing queue as a JSON array.

**Example**
```sql
SELECT indexing_queue();
```

**Depth**
- Useful when you want a single JSON blob instead of row-wise queue inspection
- Prefer `processing_queue()` for SQL-native filtering, grouping, and age analysis
