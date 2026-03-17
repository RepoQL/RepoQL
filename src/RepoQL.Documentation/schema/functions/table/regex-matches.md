---
description: "regex_matches(pattern, scope, max_results) → uri, line_number, line_content"
tags: ["regex_matches", "regex", "text-search", "content"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# regex_matches

Search live file content using .NET regular expressions.

## Capsule: RegexMatches

**Invariant**
`regex_matches(pattern, scope, max_results)` applies .NET regex semantics to current file content.

**Example**
```sql
SELECT uri, line_number, line_content
FROM regex_matches('class\\s+\\w+Udf', 'src/**/*.cs', 20);
```
//BOUNDARY: Uses .NET regex (System.Text.RegularExpressions), not RE2. Reads live files. Case-sensitive by default. 5-second timeout per file.

**Depth**
- Prefix the pattern with `(?i)` for case-insensitive matching
- `truncated_warning` is also used for invalid patterns and per-file regex timeouts, not just result caps
