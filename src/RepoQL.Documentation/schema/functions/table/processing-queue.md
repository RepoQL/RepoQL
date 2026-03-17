---
description: "processing_queue() → uri, stage, status, age_seconds for queued items"
tags: ["processing_queue", "operations", "indexing", "queue"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# processing_queue

Return a live snapshot of queued and in-flight indexing items.

## Capsule: ProcessingQueue

**Invariant**
`processing_queue()` shows what the indexer is currently doing right now.

**Example**
```sql
SELECT uri, stage, status, age_seconds
FROM processing_queue()
WHERE age_seconds > 60
ORDER BY age_seconds DESC;
```
//BOUNDARY: Returns empty when queue is idle. This is a live snapshot - results change between calls.

**Depth**
- `stage` mixes queue names such as `HotPath` with operation names such as `classification` and `analysis`
- `mime_type` may be NULL for items that have not reached classification yet
