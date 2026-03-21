---
description: "search_symbol(q, k, scope, kind_filter) → uri, symbol, kind, headline, line_start, line_end, score, confidence"
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

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `uri` | VARCHAR | Document URI containing the symbol |
| `symbol` | VARCHAR | Fully qualified symbol name |
| `kind` | VARCHAR | Symbol kind (e.g. `method`, `class`, `property`) |
| `headline` | VARCHAR | One-line summary of the symbol |
| `line_start` | INTEGER | Start line (1-based) |
| `line_end` | INTEGER | End line (1-based) |
| `score` | DOUBLE | Match score |
| `confidence` | DECIMAL(3,2) | Match confidence (0.00–1.00) |

**Depth**
- `scope` uses glob syntax such as `src/**/*.cs`, not SQL `LIKE`
- `kind_filter` is substring-based, so `'type'` matches concrete language-specific type kinds
