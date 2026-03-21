---
description: "search(keywords, scope, boost_pattern, k) → uri, headline, structure, source, sem_score, bm25_score, struct_mentions, body_mentions, deranked, score"
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

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `uri` | VARCHAR | Document URI |
| `headline` | VARCHAR | One-line x-ray summary |
| `structure` | VARCHAR | Signatures/outline (no bodies) |
| `source` | VARCHAR | Dominant ranking signal (e.g. `semantic`, `lexical`, `mixed`) |
| `sem_score` | DOUBLE | Semantic similarity score (NULL while embeddings load) |
| `bm25_score` | DOUBLE | Lexical BM25 score |
| `struct_mentions` | BIGINT | Keyword mentions in structure/headlines |
| `body_mentions` | BIGINT | Keyword mentions in document body |
| `deranked` | BOOLEAN | Whether a negative_pattern demoted this result |
| `score` | DOUBLE | Final combined score (use this for ranking) |

**Depth**
- `scope` uses glob patterns (e.g. `src/**/*.cs`, `github://dotnet/aspire/**`)
- `sem_score` can be NULL while embeddings are still loading; lexical ranking still works
- `headline` and `structure` are pre-computed x-ray summaries, so you often do not need to read the file next
