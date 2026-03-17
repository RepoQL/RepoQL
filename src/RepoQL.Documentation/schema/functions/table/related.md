---
description: "related(seed_uri, k, mode, uri_glob) → uri, headline, score"
tags: ["related", "semantic", "similarity", "search"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# related

Find documents similar to a seed URI.

## Capsule: Related

**Invariant**
`related(seed_uri, k, mode, uri_glob)` is a "more like this" query over indexed documents.

**Example**
```sql
SELECT uri, score
FROM related('file:///src/Auth.cs', k := 10);
```
//BOUNDARY: "More like this" query. Uses seed's embedding for similarity.

**Depth**
- Excludes the seed document from the results
- Falls back to lexical signals when semantic signals are unavailable
