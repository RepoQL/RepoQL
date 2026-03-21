---
description: "json_files(pattern) → uri, shape, key_count, byte_size, token_count"
tags: ["json_files", "json", "inventory", "shape"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# json_files

Inventory indexed JSON files with shape metadata.

## Capsule: JsonFiles

**Invariant**
`json_files(pattern)` returns indexed JSON documents rather than reparsing live file content.

**Example**
```sql
SELECT uri, shape, key_count, byte_size
FROM json_files('**');
```
//BOUNDARY: Returns files with JSON media type from the index. Shape metadata is computed during indexing.

**Depth**
- `shape` is the top-level structure: `object`, `array`, `value`, `empty`, or NULL for omitted shape summaries
- `token_count` may be NULL on large files where full content was omitted at index time
