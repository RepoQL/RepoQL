---
description: "search_symbol(q, k, scope, kind_filter) → uri, symbol, kind, score"
tags: ["search_symbol", "symbol", "search", "bm25"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# search_symbol

Search for functions, classes, methods, and other indexed objects by name.

## Capsule: SearchSymbol

**Invariant**
`search_symbol(q, k, scope, kind_filter)` returns objects within files, not document results.

**Example**
```sql
SELECT symbol, uri
FROM search_symbol('Service', kind_filter := 'type', scope := 'src/**/*.cs');
```
//BOUNDARY: Returns objects within files, not files themselves.

**Depth**
- `scope` uses glob syntax such as `src/**/*.cs`, not SQL `LIKE`
- `kind_filter` is substring-based, so `'type'` matches concrete language-specific type kinds
