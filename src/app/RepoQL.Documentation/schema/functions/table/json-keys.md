---
description: "json_keys(file_pattern, key_pattern) → path, name, depth, value_kind, value"
tags: ["json_keys", "json", "paths", "navigation"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# json_keys

Flatten JSON structure into addressable paths and scalar values.

## Capsule: JsonKeys

**Invariant**
`json_keys(file_pattern, key_pattern)` filters JSON pointer paths using SQL `LIKE`, not key-name matching.

**Example**
```sql
SELECT file_uri, path, value
FROM json_keys('**/*config*', '%version%');
```
//BOUNDARY: `key_pattern` is SQL LIKE on the JSON pointer path (e.g., `'%scripts%'` matches `/scripts` and `/scripts/dev`), NOT a key name filter.

**Depth**
- Returns `key_uri` so matched keys can be addressed back into the source document
- `value` is NULL for objects and arrays; use `value_kind` to distinguish leaves from containers
