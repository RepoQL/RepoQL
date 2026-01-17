# Graph-Derived Ranking Features

> Enrich search ranking with structural signals from the code graph

## Problem

Current search ranking uses primarily text-based signals:
- BM25 lexical score
- Embedding cosine similarity
- Maybe file recency

This misses structural importance signals that experienced developers intuitively use:
- "This file is imported by everything" → probably important
- "This is a leaf node with no dependents" → probably less central
- "This connects two major subsystems" → high bridging value

## Proposed Solution

Extract graph-based features and incorporate them into ranking:

```
┌─────────────────────────────────────────────────────────────────┐
│              Graph Feature Extraction                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   For each candidate document, compute:                          │
│                                                                  │
│   ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│   │   Centrality    │  │   Proximity     │  │   Structural    │ │
│   │   Features      │  │   Features      │  │   Features      │ │
│   ├─────────────────┤  ├─────────────────┤  ├─────────────────┤ │
│   │ • In-degree     │  │ • PPR to seeds  │  │ • Is interface  │ │
│   │ • Out-degree    │  │ • Hop distance  │  │ • Is test       │ │
│   │ • PageRank      │  │ • Path count    │  │ • Is entry point│ │
│   │ • Betweenness   │  │                 │  │ • Cluster coeff │ │
│   └─────────────────┘  └─────────────────┘  └─────────────────┘ │
│                                                                  │
│   Combined: text_score * f(graph_features)                       │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Feature Definitions

### Centrality Features (Precomputable)

| Feature | Definition | Signal |
|---------|------------|--------|
| `in_degree` | Number of incoming edges | "How many things use this?" |
| `out_degree` | Number of outgoing edges | "How many dependencies?" |
| `pagerank` | Global PageRank score | "Overall importance" |
| `hub_score` | HITS hub score | "Good at pointing to authorities" |
| `authority_score` | HITS authority score | "Pointed to by good hubs" |

```sql
-- Precompute centrality features
CREATE TABLE node_centrality AS
WITH degree_stats AS (
    SELECT
        n.uri,
        COUNT(DISTINCT e_in.source_uri) as in_degree,
        COUNT(DISTINCT e_out.target_uri) as out_degree
    FROM node n
    LEFT JOIN edge e_in ON n.uri = e_in.target_uri
    LEFT JOIN edge e_out ON n.uri = e_out.source_uri
    GROUP BY n.uri
)
SELECT
    uri,
    in_degree,
    out_degree,
    in_degree + out_degree as total_degree,
    CASE WHEN in_degree > 0 THEN log(in_degree) ELSE 0 END as log_in_degree
FROM degree_stats;
```

### Proximity Features (Query-Time)

| Feature | Definition | Signal |
|---------|------------|--------|
| `ppr_score` | PPR from query seeds | "Structurally related to query" |
| `min_hop_distance` | Shortest path to any seed | "How close in graph?" |
| `shared_neighbors` | Common neighbors with seeds | "Sibling relationship" |

```sql
-- Compute proximity to search seeds
CREATE MACRO proximity_features(seed_uris, target_uri) AS (
    SELECT
        (SELECT score FROM ppr_expand(seed_uris, 0.15, 5, 1000) WHERE uri = target_uri) as ppr_score,
        (SELECT MIN(distance) FROM shortest_paths(seed_uris, target_uri)) as min_hops,
        (SELECT COUNT(*) FROM shared_neighbors(seed_uris, target_uri)) as shared_count
);
```

### Structural Features (From Node Metadata)

| Feature | Source | Signal |
|---------|--------|--------|
| `is_interface` | `node.kind = 'interface'` | Interfaces often more important |
| `is_test` | `uri LIKE '%test%'` | Tests less relevant for understanding |
| `is_entrypoint` | `in_degree = 0 AND out_degree > 0` | Entry points are navigation anchors |
| `is_leaf` | `out_degree = 0` | Leaf nodes are endpoints |
| `symbol_count` | Count of symbols in file | Complexity indicator |

## Feature Combination Strategies

### Option 1: Linear Combination

```sql
final_score =
    w1 * text_score
    + w2 * log(1 + in_degree)
    + w3 * ppr_score
    + w4 * (1 - is_test * 0.3)  -- Penalize tests slightly
```

### Option 2: Multiplicative Boost

```sql
final_score = text_score * (1 + 0.1 * log(1 + in_degree)) * (1 + 0.2 * ppr_score)
```

### Option 3: Re-ranking with LTR

Train a LambdaMART model on the combined feature vector:
```python
features = [
    text_score,
    bm25_score,
    in_degree,
    out_degree,
    pagerank,
    ppr_score,
    min_hops,
    is_interface,
    is_test,
    recency_days,
    churn_count,
]
```

## Implementation: Enhanced Search

```sql
CREATE MACRO search_with_graph_features(query, k) AS (
    WITH text_results AS (
        SELECT uri, score as text_score
        FROM search(query, k := k * 3)  -- Over-retrieve for reranking
    ),
    seeds AS (
        SELECT array_agg(uri) as uris
        FROM (SELECT uri FROM text_results ORDER BY text_score DESC LIMIT 3)
    ),
    with_features AS (
        SELECT
            t.uri,
            t.text_score,
            COALESCE(c.in_degree, 0) as in_degree,
            COALESCE(c.pagerank, 0) as pagerank,
            COALESCE(p.ppr_score, 0) as ppr_score,
            CASE WHEN t.uri LIKE '%test%' OR t.uri LIKE '%Test%' THEN 1 ELSE 0 END as is_test
        FROM text_results t
        LEFT JOIN node_centrality c ON t.uri = c.uri
        LEFT JOIN ppr_expand((SELECT uris FROM seeds), 0.15, 5, 100) p ON t.uri = p.uri
    )
    SELECT
        uri,
        text_score,
        -- Combined score with graph features
        text_score
            * (1 + 0.1 * log(1 + in_degree))
            * (1 + 0.15 * ppr_score)
            * (1 - 0.2 * is_test)
        as final_score
    FROM with_features
    ORDER BY final_score DESC
    LIMIT k
);
```

## Precomputation Strategy

| Feature Type | When to Compute | Storage |
|--------------|-----------------|---------|
| Centrality (PageRank, degree) | On index build | `node_centrality` table |
| Structural (is_test, is_interface) | On index build | `node` metadata |
| Proximity (PPR, hops) | Query time | Computed on demand |

### Incremental Updates

```sql
-- Update centrality after file changes
CREATE PROCEDURE refresh_centrality(changed_uris VARCHAR[]) AS $$
    -- Only recompute for changed nodes and their neighbors
    WITH affected AS (
        SELECT DISTINCT uri
        FROM (
            SELECT unnest(changed_uris) as uri
            UNION
            SELECT target_uri FROM edge WHERE source_uri = ANY(changed_uris)
            UNION
            SELECT source_uri FROM edge WHERE target_uri = ANY(changed_uris)
        )
    )
    UPDATE node_centrality c
    SET in_degree = (SELECT COUNT(*) FROM edge WHERE target_uri = c.uri),
        out_degree = (SELECT COUNT(*) FROM edge WHERE source_uri = c.uri)
    WHERE c.uri IN (SELECT uri FROM affected);
$$;
```

## Expected Impact

| Scenario | Without Graph Features | With Graph Features |
|----------|------------------------|---------------------|
| Query: "config" | Returns all config files equally | Boosts widely-imported ConfigService |
| Query: "auth" | Tests rank alongside implementation | Implementation ranked higher |
| Query: "utils" | Random utility files | Central utilities boosted |

## Open Questions

1. How to tune feature weights? (A/B test? Manual? LTR training?)
2. Should centrality features be normalized per-repository?
3. Cost of PPR at query time - cache top-k seeds?

## References

- [TwoStageRanking.md](../research/algorithms/TwoStageRanking.md) - LTR and feature engineering
- [GraphRanking.md](../research/algorithms/GraphRanking.md) - Centrality algorithms
