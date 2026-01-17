# PPR-Based Context Expansion

> Use Personalized PageRank to find structurally-related code from search hits

## Problem

Text search finds content that matches keywords/semantics, but misses structurally-related code:

- Search "JWT validation" finds `JwtValidator.cs`
- But misses `AuthMiddleware.cs` (caller), `TokenConfig.cs` (config), `AuthTests.cs` (tests)

These related files don't contain "JWT" but are essential for understanding.

## Proposed Solution

After text search returns seed results, run PPR from those seeds on the code graph to discover related nodes:

```
┌─────────────────────────────────────────────────────────────────┐
│                  Search + PPR Pipeline                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "JWT validation"                                        │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ Text Search  │ → JwtValidator.cs (seed)                      │
│   └──────────────┘                                               │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐     AuthMiddleware.cs (calls JwtValidator)    │
│   │     PPR      │ →   TokenConfig.cs (imported by JwtValidator) │
│   │  Expansion   │     IAuthService.cs (interface)               │
│   └──────────────┘     AuthController.cs (entry point)           │
│       │                                                          │
│       ▼                                                          │
│   Combined results (text relevance + graph proximity)            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Implementation Sketch

### Using USING KEY for Efficient PPR

```sql
-- PPR expansion from seed nodes
CREATE MACRO ppr_expand(seed_uris, alpha, max_iter, top_k) AS (
    WITH RECURSIVE ppr (uri, score, iter) AS (
        -- Initialize: seed nodes with equal probability
        SELECT uri, 1.0 / array_length(seed_uris), 0
        FROM unnest(seed_uris) as t(uri)

        UNION ALL

        -- Iterate with restart
        SELECT
            e.target_uri as uri,
            SUM(
                alpha * (CASE WHEN e.target_uri = ANY(seed_uris) THEN 1.0/array_length(seed_uris) ELSE 0 END)
                + (1-alpha) * (p.score / NULLIF(out_deg.cnt, 0))
            ) as score,
            p.iter + 1
        FROM ppr p
        JOIN edge e ON p.uri = e.source_uri
        JOIN (SELECT source_uri, COUNT(*) as cnt FROM edge GROUP BY source_uri) out_deg
            ON e.source_uri = out_deg.source_uri
        WHERE p.iter < max_iter
        GROUP BY e.target_uri
    ) USING KEY (uri)

    SELECT uri, score
    FROM ppr
    ORDER BY score DESC
    LIMIT top_k
);
```

### Integration with search()

```sql
-- Enhanced search with graph expansion
CREATE MACRO search_with_context(query, k, expand_k) AS (
    WITH text_hits AS (
        SELECT uri, score as text_score
        FROM search(query, k := k)
    ),
    seeds AS (
        SELECT array_agg(uri) as uris FROM text_hits
    ),
    graph_expanded AS (
        SELECT uri, score as graph_score
        FROM ppr_expand((SELECT uris FROM seeds), 0.15, 10, expand_k)
    )
    SELECT
        COALESCE(t.uri, g.uri) as uri,
        COALESCE(t.text_score, 0) as text_score,
        COALESCE(g.graph_score, 0) as graph_score,
        0.7 * COALESCE(t.text_score, 0) + 0.3 * COALESCE(g.graph_score, 0) as combined
    FROM text_hits t
    FULL OUTER JOIN graph_expanded g ON t.uri = g.uri
    ORDER BY combined DESC
);
```

## Edge Types to Consider

| Edge Type | Include in PPR? | Weight | Rationale |
|-----------|-----------------|--------|-----------|
| `calls` | Yes | 1.0 | Strong semantic relationship |
| `imports` | Yes | 0.8 | Dependency relationship |
| `implements` | Yes | 0.5 | Type relationship |
| `tests` | Optional | 0.6 | Tests reveal behavior |
| `references` | Yes | 0.7 | Usage relationship |

### Configurable Edge Filtering

```sql
-- PPR with edge type filtering
WHERE e.type IN ('calls', 'imports', 'implements')
  AND e.weight >= 0.5
```

## Use Cases

| Scenario | Seeds | PPR Finds |
|----------|-------|-----------|
| "How does auth work?" | AuthService | Callers, dependencies, tests |
| "Fix bug in parser" | Parser.cs | Related transformers, consumers |
| "Understand config system" | Config.cs | All config consumers, loaders |

## Expected Benefits

1. **Complete context**: Agents see related code without explicit queries
2. **Discover unknowns**: Find files user didn't know to search for
3. **Structural understanding**: Results reflect code architecture

## Performance Considerations

- **USING KEY**: Essential for performance (100x+ speedup over standard CTE)
- **Iteration limit**: 5-10 iterations sufficient for most graphs
- **Top-k pruning**: Keep only top-100 at each iteration to bound memory
- **Caching**: Cache PPR vectors for frequently-accessed seed nodes

## Open Questions

1. How to weight text relevance vs graph proximity (the 0.7/0.3 split)?
2. Should PPR run bidirectionally (callers AND callees)?
3. Edge type weights - tune per query type or fixed?

## References

- [GraphRanking.md](../research/algorithms/GraphRanking.md) - Full PPR theory
- [DuckDB USING KEY](https://duckdb.org/2025/05/23/using-key) - Performance optimization
