---
description: "match_score(query, target) → fuzzy match score (VARCHAR, cast to FLOAT for comparison)"
tags: ["match_score", "fuzzy", "ranking"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# match_score

Compute a fuzzy subsequence match score between two strings.

## Capsule: MatchScore

**Invariant**
`match_score(query, target)` returns a fuzzy score as text for ranking approximate matches.

**Example**
```sql
SELECT name, match_score('ops', name) as score
FROM Functions
WHERE CAST(match_score('ops', name) AS FLOAT) > 0
ORDER BY CAST(match_score('ops', name) AS FLOAT) DESC
LIMIT 10;
```

**Depth**
- Returns `VARCHAR` containing a float, so cast to `FLOAT` for numeric comparison or ordering
- Higher means closer subsequence match; `"0.0000"` means no subsequence match
