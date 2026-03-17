---
description: "json_data(uri) → live-parsed JSON rows with dynamic DuckDB schema"
tags: ["json_data", "json", "typed", "dynamic-schema"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# json_data

Parse a live JSON file into a DuckDB-typed table.

## Capsule: JsonData

**Invariant**
`json_data(uri)` reads the current file and infers a DuckDB schema from the JSON structure.

**Example**
```sql
SELECT name, version, type
FROM json_data('file:///dashboard/package.json');
```
//BOUNDARY: Live file read - always returns current content. Schema varies per file; columns are inferred from JSON structure. Only works with `file:///` URIs.

**Depth**
- Top-level objects become one row; arrays become one row per element
- Nested objects are returned as DuckDB `STRUCT`s and can be accessed with dot notation
