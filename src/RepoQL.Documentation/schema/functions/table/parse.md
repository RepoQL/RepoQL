---
description: "parse(text) → auto-detected format rows with dynamic columns"
tags: ["parse", "json", "csv", "yaml", "inline-data"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# parse

Parse inline structured text into rows with auto-detected format.

## Capsule: Parse

**Invariant**
`parse(text)` detects common formats and returns rows without requiring an external file.

**Example**
```sql
SELECT *
FROM parse('id,name,score
1,Alice,95
2,Bob,87');
```
//BOUNDARY: CSV/TSV require 2+ columns and 2+ data rows to avoid false positives on prose.

**Depth**
- Detection order is JSON, JSONL, TSV, CSV, YAML, embedded data, then structured text
- Type inference promotes numbers, booleans, and floats automatically
