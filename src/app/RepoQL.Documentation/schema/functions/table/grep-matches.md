---
description: "grep_matches(pattern, scope, max_results) → uri, line_number, line_content"
tags: ["grep_matches", "grep", "text-search", "content"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# grep_matches

Search live file content for literal text matches.

## Capsule: GrepMatches

**Invariant**
`grep_matches(pattern, scope, max_results)` performs case-insensitive literal text search against current file content.

**Example**
```sql
SELECT uri, line_number, line_content
FROM grep_matches('StructuredUdf', 'src/**/*.cs', 20);
```
//BOUNDARY: Reads current file content for URIs known to the registry. Case-insensitive. Returns at most `max_results` matches across all files.

**Depth**
- Returns a `truncated_warning` column when results are capped
- Use it for exact-text lookups; use `regex_matches()` when you need pattern semantics
