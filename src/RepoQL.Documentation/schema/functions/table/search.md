---
description: "search(keywords, scope, boost_pattern, k) → uri, headline, score, sem_score, bm25_score"
tags: ["search", "semantic", "bm25", "hybrid"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# search

Search indexed documents with hybrid semantic and lexical ranking.

## Capsule: Search

**Invariant**
`search(keywords, scope, boost_pattern, k)` returns documents, not symbols.

**Example**
```sql
SELECT uri, headline, score
FROM search('error handling', k := 10);
```
//BOUNDARY: Returns documents (files), not individual symbols. Use `snippet()` to get code context.

**Depth**
- `scope` uses glob patterns (e.g. `src/**/*.cs`, `github://dotnet/aspire/**`)
- `sem_score` can be NULL while embeddings are still loading; lexical ranking still works
- `headline` and `structure` are pre-computed x-ray summaries, so you often do not need to read the file next
