# MMR-Based Diversity Selection for Search and Explore

> Reduce redundancy in search results and explore summaries using Maximal Marginal Relevance

## Problem

Current search and explore return results ranked purely by relevance score. This leads to:

1. **Redundant results**: Top-5 hits often cover the same concept from slightly different angles
2. **Wasted token budget**: Similar files consume budget without adding information
3. **Coverage gaps**: Important but lower-scored results get excluded

Example: Query "authentication" returns 5 files all about JWT validation, missing OAuth, session management, and middleware.

## Proposed Solution

Implement MMR (Maximal Marginal Relevance) selection in the search and explore pipelines:

```
MMR(d) = λ * relevance(d, query) - (1-λ) * max_similarity(d, already_selected)
```

Instead of top-k by score, greedily select items that balance relevance against redundancy.

## Implementation Sketch

### SQL Macro Approach

```sql
-- mmr_select(candidates, k, lambda) -> diverse top-k
CREATE MACRO mmr_select(query_embedding, k, lambda) AS (
    WITH RECURSIVE mmr AS (
        -- First item: highest relevance
        SELECT uri, embedding, relevance, 1 as rank
        FROM candidates
        ORDER BY relevance DESC
        LIMIT 1

        UNION ALL

        -- Subsequent items: balance relevance vs similarity to selected
        SELECT
            c.uri, c.embedding, c.relevance,
            m.rank + 1
        FROM candidates c, mmr m
        WHERE c.uri NOT IN (SELECT uri FROM mmr)
          AND m.rank < k
        ORDER BY (
            lambda * c.relevance
            - (1-lambda) * (SELECT MAX(cosine_similarity(c.embedding, s.embedding)) FROM mmr s)
        ) DESC
        LIMIT 1
    )
    SELECT uri, relevance, rank FROM mmr
);
```

### Integration Points

| Component | Current | With MMR |
|-----------|---------|----------|
| `search()` | Top-k by score | MMR selection with λ=0.7 |
| `explore` (Explore intent) | Top files by relevance | MMR to ensure breadth |
| `explore` (Find intent) | Ranked list | MMR with higher λ=0.8 (precision matters) |
| Token budget allocation | Greedy by score | Cost-weighted MMR |

### Lambda Tuning by Intent

| Intent | Recommended λ | Rationale |
|--------|---------------|-----------|
| Explore | 0.5-0.6 | Maximum diversity for discovery |
| Find | 0.7-0.8 | Balance precision with coverage |
| Examine | 0.9 | Relevance dominant, some diversity |
| Understand | 0.6-0.7 | Need diverse evidence for synthesis |

## Expected Benefits

- **Better coverage**: Top-10 MMR results cover more distinct topics than top-10 by score
- **Token efficiency**: Less redundant content in context window
- **User satisfaction**: Answers feel more complete

## Complexity

- **Computational**: O(k² * n) for k selections from n candidates (acceptable for k < 100)
- **Embedding storage**: Already have embeddings in `document_embedding`
- **Similarity computation**: Cosine similarity is fast, can use DuckDB's `array_cosine_similarity`

## Open Questions

1. Should λ be user-configurable or auto-tuned per query type?
2. How to handle cost weighting (longer files penalized)?
3. Cache similarity matrix for frequent candidate sets?

## References

- [BudgetedSelection.md](../research/algorithms/BudgetedSelection.md) - Full MMR theory and implementation
- Carbonell & Goldstein (1998) - Original MMR paper
