---
description: "json_preview(uri, rows) → live-parsed JSON rows with row limit"
tags: ["json_preview", "json", "preview", "dynamic-schema"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# json_preview

Preview a limited number of rows from a live JSON file.

## Capsule: JsonPreview

**Invariant**
`json_preview(uri, rows)` is `json_data()` with an explicit row cap for arrays.

**Example**
```sql
SELECT *
FROM json_preview('file:///data/items.json', 3);
```
//BOUNDARY: Same as `json_data()` - live read, dynamic schema, `file:///` URIs only. The rows parameter limits array elements, not object keys.

**Depth**
- The row limit has no practical effect for single JSON objects
- Use it to sample large arrays before running full `json_data()` queries
