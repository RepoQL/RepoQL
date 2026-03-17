---
description: "glob_match(path, pattern) → boolean"
tags: ["glob_match", "glob", "filtering"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# glob_match

Test whether a path or URI matches a glob pattern.

## Capsule: GlobMatch

**Invariant**
`glob_match(path, pattern)` returns true when the input matches the glob expression.

**Example**
```sql
SELECT uri
FROM node
WHERE kind = 'document' AND glob_match(uri, '**/*.md');
```

**Depth**
- Useful inside `WHERE` clauses when you need glob semantics without expanding to a table function
- Keeps pattern logic in SQL instead of pushing it out to client-side filtering
