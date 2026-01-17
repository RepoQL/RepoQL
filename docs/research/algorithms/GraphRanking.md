# Graph Ranking Algorithms for Code Navigation

> Reference documentation for graph-based ranking and traversal algorithms in code relationship graphs

## Table of Contents

1. [Overview](#overview)
2. [Personalized PageRank (PPR) / Random Walk with Restart](#personalized-pagerank-ppr--random-walk-with-restart)
3. [Graph Embeddings: DeepWalk and node2vec](#graph-embeddings-deepwalk-and-node2vec)
4. [Steiner Tree / Group Steiner Tree](#steiner-tree--group-steiner-tree)
5. [DuckDB Implementation](#duckdb-implementation)
6. [Application to Code Graphs](#application-to-code-graphs)
7. [Combining Graph Proximity with Text Relevance](#combining-graph-proximity-with-text-relevance)
8. [Scalability Considerations](#scalability-considerations)
9. [Best Practices and Common Pitfalls](#best-practices-and-common-pitfalls)
10. [References](#references)

---

## Overview

Code navigation is fundamentally **graph traversal**: understanding a codebase means navigating relationships between entities—functions call functions, classes inherit from classes, modules import modules.

```
┌─────────────────────────────────────────────────────────────────┐
│                    Code Graph Types                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   CALL GRAPH              IMPORT GRAPH           DEPENDENCY GRAPH│
│                                                                  │
│   ┌────┐                  ┌────┐                 ┌────┐         │
│   │main│──┐               │ A  │◄──┐             │ A  │         │
│   └────┘  │               └────┘   │             └──┬─┘         │
│           ▼                        │                │           │
│       ┌──────┐            ┌────┐   │             ┌──▼─┐         │
│       │authFn│───┐        │ B  │───┘             │ B  │         │
│       └──────┘   │        └────┘                 └──┬─┘         │
│                  ▼               ▲                  │           │
│              ┌──────┐     ┌────┐ │               ┌──▼─┐         │
│              │jwtLib│     │ C  │─┘               │ C  │         │
│              └──────┘     └────┘                 └────┘         │
│                                                                  │
│   "What calls this?"     "What imports this?"   "What depends   │
│                                                  on this?"       │
└─────────────────────────────────────────────────────────────────┘
```

### Why Graph Ranking Matters

| Question Type | Graph Algorithm | Use Case |
|---------------|-----------------|----------|
| "What is most related to X?" | Personalized PageRank | Find relevant context from seed |
| "What connects A and B?" | Steiner Tree | Explain relationships |
| "What are similar functions?" | Graph Embeddings | Clustering, recommendation |
| "What is the impact of changing X?" | Reverse PPR / BFS | Impact analysis |
| "What is the entry point?" | PageRank centrality | Find important nodes |

### Graph Ranking vs. Text Search

| Aspect | Text Search | Graph Ranking |
|--------|-------------|---------------|
| Query | Keywords/embeddings | Seed node(s) |
| Relevance | Content similarity | Structural proximity |
| Result | Documents | Connected subgraph |
| Strength | Finding content | Finding relationships |

**Key insight**: Combine both for optimal code search—use text search to find seeds, then graph ranking to expand context.

---

## Personalized PageRank (PPR) / Random Walk with Restart

PPR answers: "Starting from seed node(s), what other nodes are most reachable/relevant?"

### The Algorithm

```
┌─────────────────────────────────────────────────────────────────┐
│               Random Walk with Restart                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Start at seed node S                                           │
│       │                                                          │
│       ▼                                                          │
│   ┌─────────────────────────────────────────┐                    │
│   │  At each step:                          │                    │
│   │                                         │                    │
│   │  With probability α (restart):          │                    │
│   │      Return to seed S                   │◄─────────┐        │
│   │                                         │          │        │
│   │  With probability (1-α) (continue):     │          │        │
│   │      Move to random neighbor            │──────────┘        │
│   │                                         │   restart         │
│   └─────────────────────────────────────────┘                    │
│                                                                  │
│   PPR(v) = long-run probability of being at node v               │
│                                                                  │
│   Nodes frequently visited = structurally close to seed          │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Mathematical Formulation

```
PPR = α * s + (1-α) * A * PPR

Where:
  - PPR is the personalized PageRank vector
  - α is the restart probability (typically 0.15)
  - s is the seed/personalization vector (one-hot or distribution)
  - A is the column-normalized adjacency matrix

Solved iteratively:
  PPR^(t+1) = α * s + (1-α) * A * PPR^(t)

Until convergence: ||PPR^(t+1) - PPR^(t)|| < ε
```

### Restart Probability (α) Effects

| α Value | Behavior | Use Case |
|---------|----------|----------|
| 0.05 | Explore far, slow convergence | Global structure discovery |
| 0.15 | Standard balance | General navigation |
| 0.30 | Stay close to seed | Local neighborhood |
| 0.50 | Very local focus | Immediate dependencies |

### Node-Dependent Restart (arXiv:1408.0719)

The standard PPR uses constant restart probability. The generalization allows **node-dependent restart**:

```
α(v) varies by node type:
  - α(function) = 0.15  (standard exploration)
  - α(test) = 0.30      (tests are endpoints, don't explore far)
  - α(interface) = 0.10 (interfaces connect many things)
```

This allows the random walk to behave differently based on node semantics.

### PPR for Code Navigation

```sql
-- Find nodes most related to AuthService using PPR
WITH RECURSIVE ppr AS (
    -- Seed: start at AuthService
    SELECT
        'AuthService' as node_id,
        1.0 as score,
        0 as iteration

    UNION ALL

    -- Iterate: random walk step
    SELECT
        e.target as node_id,
        SUM(
            0.15 * (CASE WHEN e.target = 'AuthService' THEN 1.0 ELSE 0.0 END)
            + 0.85 * (p.score / out_degree.cnt)
        ) as score,
        p.iteration + 1 as iteration
    FROM ppr p
    JOIN edge e ON p.node_id = e.source
    JOIN (
        SELECT source, COUNT(*) as cnt
        FROM edge
        GROUP BY source
    ) out_degree ON e.source = out_degree.source
    WHERE p.iteration < 20  -- Max iterations
    GROUP BY e.target
)
SELECT node_id, score
FROM ppr
WHERE iteration = (SELECT MAX(iteration) FROM ppr)
ORDER BY score DESC
LIMIT 20;
```

---

## Graph Embeddings: DeepWalk and node2vec

Graph embeddings map nodes to dense vectors where **graph proximity ≈ vector similarity**.

### DeepWalk (arXiv:1403.6652)

DeepWalk treats random walks as "sentences" and nodes as "words", then applies Word2Vec:

```
┌─────────────────────────────────────────────────────────────────┐
│                    DeepWalk Pipeline                             │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   1. Generate Random Walks                                       │
│      ┌───┐    ┌───┐    ┌───┐    ┌───┐    ┌───┐                  │
│      │ A │───▶│ B │───▶│ D │───▶│ E │───▶│ F │  (walk 1)        │
│      └───┘    └───┘    └───┘    └───┘    └───┘                  │
│                                                                  │
│      ┌───┐    ┌───┐    ┌───┐    ┌───┐    ┌───┐                  │
│      │ A │───▶│ C │───▶│ D │───▶│ B │───▶│ A │  (walk 2)        │
│      └───┘    └───┘    └───┘    └───┘    └───┘                  │
│                                                                  │
│   2. Train Skip-gram (Word2Vec)                                  │
│      Context window slides over walks                            │
│      Predict neighbors from center node                          │
│                                                                  │
│   3. Result: Node Embeddings                                     │
│      A → [0.2, -0.5, 0.8, ...]                                   │
│      B → [0.3, -0.4, 0.7, ...]  (similar to A)                   │
│      C → [-0.1, 0.9, 0.2, ...]  (different cluster)              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### node2vec: Biased Random Walks

node2vec extends DeepWalk with **biased walks** controlled by parameters p and q:

```
┌─────────────────────────────────────────────────────────────────┐
│               node2vec Transition Probabilities                  │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Previous: t    Current: v    Next: x                           │
│                                                                  │
│       ┌───┐         ┌───┐         ┌───┐                         │
│       │ t │─────────│ v │─────────│ x │                         │
│       └───┘         └─┬─┘         └───┘                         │
│         ▲             │             ▲                           │
│         │             │             │                           │
│    1/p  │      1      │      1/q    │                           │
│  (return)    (stay)      (explore)                              │
│                                                                  │
│   Transition probability from v to x:                            │
│                                                                  │
│   α(t,x) = 1/p  if d(t,x) = 0  (return to previous)             │
│          = 1    if d(t,x) = 1  (neighbor of both t and v)       │
│          = 1/q  if d(t,x) = 2  (explore away from t)            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Parameter Effects

| p | q | Behavior | Analogous To |
|---|---|----------|--------------|
| 1 | 1 | Uniform (DeepWalk) | Pure random walk |
| Low | High | BFS-like (local) | Explore neighborhood |
| High | Low | DFS-like (global) | Explore far paths |
| 0.5 | 2 | Balanced local | Community detection |
| 2 | 0.5 | Balanced global | Role detection |

### Comparison

| Aspect | DeepWalk | node2vec |
|--------|----------|----------|
| Walk strategy | Uniform random | Biased (p, q) |
| Flexibility | Fixed | Tunable local/global |
| Speed | Faster | Slower (bias computation) |
| Best for | Homogeneous graphs | Heterogeneous structures |

### Application to Code Graphs

```python
# Generate node2vec embeddings for code graph
from node2vec import Node2Vec

# Build graph from code relationships
G = build_code_graph(edges)  # call edges, import edges, etc.

# For code: BFS-like exploration (find local context)
node2vec = Node2Vec(
    G,
    dimensions=128,
    walk_length=40,
    num_walks=10,
    p=0.5,   # Moderate return
    q=2.0,   # Discourage far exploration
    workers=4
)

model = node2vec.fit(window=10, min_count=1, batch_words=4)

# Find similar functions
similar = model.wv.most_similar('AuthService.ValidateToken')
# [('AuthService.RefreshToken', 0.89), ('JwtValidator.Verify', 0.85), ...]
```

---

## Steiner Tree / Group Steiner Tree

Steiner trees answer: "What is the minimal subgraph connecting these query nodes?"

### Problem Definition

```
┌─────────────────────────────────────────────────────────────────┐
│                 Group Steiner Tree Problem                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Given:                                                         │
│   - Graph G = (V, E) with edge weights                          │
│   - Query terminals Q = {q1, q2, ..., qk}                       │
│                                                                  │
│   Find:                                                          │
│   - Minimum-weight tree T connecting all terminals               │
│                                                                  │
│   Example: Connect AuthService, UserRepository, JwtValidator     │
│                                                                  │
│       [AuthService]                                              │
│            │                                                     │
│            ▼                                                     │
│       [AuthMiddleware] ◄──── (Steiner node - not in query)      │
│         │         │                                              │
│         ▼         ▼                                              │
│   [UserRepository]  [JwtValidator]                               │
│                                                                  │
│   Result: Explanation of how query entities relate               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### STAR Algorithm (ICDE 2009)

STAR (Steiner Tree Approximation in Relationship graphs) provides efficient approximation:

```
Algorithm STAR:
1. For each terminal pair (qi, qj):
   - Find shortest path P(qi, qj)

2. Build auxiliary graph H:
   - Nodes = terminals Q
   - Edge weight = shortest path length

3. Find minimum spanning tree MST(H)

4. Replace MST edges with actual paths

5. Remove redundant edges (prune)

Approximation ratio: O(log k) where k = |Q|
```

### SQL Implementation for Path Finding

```sql
-- Find shortest path between two code entities
WITH RECURSIVE paths AS (
    SELECT
        source as start_node,
        target as current_node,
        ARRAY[source, target] as path,
        1 as depth,
        weight as total_weight
    FROM edge
    WHERE source = 'AuthService'

    UNION ALL

    SELECT
        p.start_node,
        e.target as current_node,
        array_append(p.path, e.target) as path,
        p.depth + 1,
        p.total_weight + e.weight
    FROM paths p
    JOIN edge e ON p.current_node = e.source
    WHERE e.target != ALL(p.path)  -- Avoid cycles
      AND p.depth < 10             -- Max depth
)
SELECT path, total_weight
FROM paths
WHERE current_node = 'JwtValidator'
ORDER BY total_weight ASC
LIMIT 1;
```

---

## DuckDB Implementation

DuckDB provides three approaches for graph algorithms:

### 1. Standard WITH RECURSIVE

Traditional recursive CTEs for simple traversals:

```sql
-- Find all nodes reachable from AuthService within 3 hops
WITH RECURSIVE reachable AS (
    SELECT 'AuthService' as node, 0 as depth

    UNION

    SELECT e.target, r.depth + 1
    FROM reachable r
    JOIN edge e ON r.node = e.source
    WHERE r.depth < 3
)
SELECT DISTINCT node, MIN(depth) as min_depth
FROM reachable
GROUP BY node
ORDER BY min_depth;
```

### 2. USING KEY Optimization (DuckDB 2025)

For algorithms that update state (shortest path, PPR), `USING KEY` provides dramatic speedups:

```sql
-- Shortest paths with USING KEY (Bellman-Ford style)
WITH RECURSIVE distances (node, dist) AS (
    SELECT 'AuthService' as node, 0 as dist

    UNION ALL

    -- USING KEY allows updating existing entries
    SELECT
        e.target as node,
        MIN(d.dist + e.weight) as dist
    FROM distances d
    JOIN edge e ON d.node = e.source
    GROUP BY e.target
) USING KEY (node)  -- Key column for dictionary semantics
SELECT node, dist
FROM distances
ORDER BY dist
LIMIT 20;
```

**Performance Comparison**:

| Algorithm | Standard CTE | USING KEY | Speedup |
|-----------|--------------|-----------|---------|
| Shortest Path (10K nodes) | 12.4s | 0.08s | 155x |
| Connected Components | 45.2s | 0.31s | 146x |
| PageRank (5 iterations) | 8.7s | 0.12s | 72x |
| Distance Vector Routing | 23.1s | 0.05s | 462x |

### 3. DuckPGQ (SQL/PGQ Standard)

DuckPGQ provides graph pattern matching syntax from SQL:2023:

```sql
-- Install and load DuckPGQ
INSTALL duckpgq FROM community;
LOAD duckpgq;

-- Create property graph over existing tables
CREATE PROPERTY GRAPH code_graph
VERTEX TABLES (
    nodes PROPERTIES (id, name, kind)
)
EDGE TABLES (
    edges SOURCE KEY (source) REFERENCES nodes (id)
          DESTINATION KEY (target) REFERENCES nodes (id)
          PROPERTIES (type, weight)
);

-- Pattern matching: Find call chains
SELECT *
FROM GRAPH_TABLE (code_graph
    MATCH (a:nodes)-[c:edges WHERE c.type = 'calls']->{1,3}(b:nodes)
    WHERE a.name = 'main'
    COLUMNS (a.name as caller, b.name as callee, path_length(c) as depth)
);

-- Shortest path
SELECT *
FROM GRAPH_TABLE (code_graph
    MATCH SHORTEST (a:nodes)-[e:edges]->+(b:nodes)
    WHERE a.name = 'AuthService' AND b.name = 'Database'
    COLUMNS (a.name, b.name, path_length(e) as hops)
);
```

### Choosing the Right Approach

| Use Case | Recommended | Rationale |
|----------|-------------|-----------|
| Simple BFS/DFS | Standard CTE | Straightforward |
| Shortest path | USING KEY | Dramatic speedup |
| PageRank/PPR | USING KEY | State updates needed |
| Pattern matching | DuckPGQ | Expressive syntax |
| Path finding | DuckPGQ | Built-in SHORTEST |
| Production stability | Standard CTE | Most mature |

---

## Application to Code Graphs

### Common Code Navigation Queries

| Query | Algorithm | SQL Pattern |
|-------|-----------|-------------|
| "What calls this function?" | Reverse BFS | `WHERE target = ?` |
| "What does this function call?" | Forward BFS | `WHERE source = ?` |
| "What is the call chain to X?" | Shortest path | USING KEY / DuckPGQ |
| "Related functions to X" | PPR | Iterative CTE |
| "Impact of changing X" | Reverse PPR | Seed at X, reverse edges |
| "Entry points that reach X" | Reverse reachability | Reverse BFS |

### Call Graph Analysis

```sql
-- Find all callers of a function (transitive)
WITH RECURSIVE callers AS (
    SELECT source as caller, 1 as depth
    FROM edge
    WHERE target = 'AuthService.ValidateToken'
      AND type = 'calls'

    UNION

    SELECT e.source, c.depth + 1
    FROM callers c
    JOIN edge e ON c.caller = e.target
    WHERE e.type = 'calls'
      AND c.depth < 5
)
SELECT caller, MIN(depth) as call_depth
FROM callers
GROUP BY caller
ORDER BY call_depth;
```

### Import Graph Ranking

```sql
-- Rank modules by import centrality (simplified PageRank)
WITH RECURSIVE pagerank AS (
    -- Initialize: equal probability
    SELECT
        id as node,
        1.0 / (SELECT COUNT(*) FROM modules) as score,
        0 as iteration
    FROM modules

    UNION ALL

    SELECT
        m.id as node,
        0.15 / (SELECT COUNT(*) FROM modules)  -- damping
        + 0.85 * COALESCE(SUM(p.score / out_deg.cnt), 0) as score,
        p.iteration + 1
    FROM modules m
    LEFT JOIN edge e ON m.id = e.target AND e.type = 'imports'
    LEFT JOIN pagerank p ON e.source = p.node
    LEFT JOIN (
        SELECT source, COUNT(*) as cnt
        FROM edge WHERE type = 'imports'
        GROUP BY source
    ) out_deg ON e.source = out_deg.source
    WHERE p.iteration < 10
    GROUP BY m.id
)
SELECT node, score
FROM pagerank
WHERE iteration = (SELECT MAX(iteration) FROM pagerank)
ORDER BY score DESC
LIMIT 10;
```

### Dependency Impact Analysis

```sql
-- Find all modules affected by changing a dependency
WITH RECURSIVE impacted AS (
    -- Seed: the changed module
    SELECT 'utils/auth.ts' as module, 0 as distance

    UNION

    -- Expand: modules that import impacted modules
    SELECT e.source as module, i.distance + 1
    FROM impacted i
    JOIN edge e ON i.module = e.target
    WHERE e.type = 'imports'
      AND i.distance < 10
)
SELECT module, MIN(distance) as impact_distance
FROM impacted
GROUP BY module
ORDER BY impact_distance;
```

---

## Combining Graph Proximity with Text Relevance

The most effective code search combines **text relevance** (what matches the query) with **graph proximity** (what's structurally related).

### Fusion Strategies

```
┌─────────────────────────────────────────────────────────────────┐
│              Text + Graph Score Fusion                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Query: "JWT validation"                                        │
│                                                                  │
│   1. Text Search → Candidates with text_score                    │
│      ┌────────────────────────────────────────┐                 │
│      │ JwtValidator.cs         text_score=0.92│                 │
│      │ AuthMiddleware.cs       text_score=0.45│                 │
│      │ TokenService.cs         text_score=0.38│                 │
│      └────────────────────────────────────────┘                 │
│                                                                  │
│   2. Graph Expansion → PPR from top hit                         │
│      Seed: JwtValidator.cs                                       │
│      ┌────────────────────────────────────────┐                 │
│      │ AuthMiddleware.cs       ppr_score=0.35 │                 │
│      │ UserController.cs       ppr_score=0.28 │                 │
│      │ RefreshTokenHandler.cs  ppr_score=0.22 │                 │
│      └────────────────────────────────────────┘                 │
│                                                                  │
│   3. Fusion                                                      │
│      combined = α * text_score + (1-α) * ppr_score              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Fusion Methods

| Method | Formula | Best For |
|--------|---------|----------|
| **Linear** | `α * text + (1-α) * graph` | Simple, tunable |
| **Multiplicative** | `text * graph` | Require both signals |
| **Re-ranking** | PPR reranks text results | Preserve precision |
| **Query-dependent** | α varies by query type | Adaptive systems |

### Implementation

```sql
-- Combined text + graph ranking
WITH text_results AS (
    SELECT uri, score as text_score
    FROM search('JWT validation', k := 50)
),
seed_nodes AS (
    SELECT uri FROM text_results ORDER BY text_score DESC LIMIT 3
),
graph_scores AS (
    -- Simplified PPR from seed nodes
    WITH RECURSIVE ppr AS (
        SELECT uri as node, 1.0 / 3 as score, 0 as iter
        FROM seed_nodes
        UNION ALL
        SELECT e.target, 0.15 + 0.85 * SUM(p.score / deg.out_deg), p.iter + 1
        FROM ppr p
        JOIN edge e ON p.node = e.source
        JOIN (SELECT source, COUNT(*) as out_deg FROM edge GROUP BY source) deg
            ON e.source = deg.source
        WHERE p.iter < 5
        GROUP BY e.target
    )
    SELECT node as uri, MAX(score) as graph_score
    FROM ppr
    GROUP BY node
)
SELECT
    COALESCE(t.uri, g.uri) as uri,
    COALESCE(t.text_score, 0) as text_score,
    COALESCE(g.graph_score, 0) as graph_score,
    0.6 * COALESCE(t.text_score, 0) + 0.4 * COALESCE(g.graph_score, 0) as combined
FROM text_results t
FULL OUTER JOIN graph_scores g ON t.uri = g.uri
ORDER BY combined DESC
LIMIT 20;
```

---

## Scalability Considerations

### Complexity

| Algorithm | Time | Space | Notes |
|-----------|------|-------|-------|
| BFS/DFS | O(V + E) | O(V) | Linear, fast |
| PPR (power iteration) | O(k * E) | O(V) | k = iterations |
| DeepWalk | O(n * l * V) | O(V * d) | n=walks, l=length, d=dims |
| node2vec | O(n * l * V * avg_deg) | O(V * d) | Bias computation adds cost |
| Steiner Tree | NP-hard | O(V²) | Use approximations |

### Optimization Strategies

| Strategy | Speedup | Trade-off |
|----------|---------|-----------|
| **Monte Carlo PPR** | 10-100x | Approximate scores |
| **Bidirectional search** | 2-10x | Implementation complexity |
| **Precompute top-k PPR** | 1000x query | Storage, staleness |
| **Graph partitioning** | Varies | Cross-partition queries slower |
| **USING KEY** | 50-500x | DuckDB-specific |

### Codebase Size Guidelines

| Nodes | Edges | Recommended Approach |
|-------|-------|---------------------|
| <10K | <50K | Full algorithms, no optimization |
| 10K-100K | 50K-500K | USING KEY, sampling for embeddings |
| 100K-1M | 500K-5M | Precomputation, approximate PPR |
| >1M | >5M | Graph partitioning, distributed |

---

## Best Practices and Common Pitfalls

### Best Practices

| Practice | Rationale |
|----------|-----------|
| **Use edge types** | `calls` vs `imports` have different semantics |
| **Weight by recency** | Recent edges more relevant |
| **Limit traversal depth** | Code rarely needs >5 hops |
| **Cache PPR for hot nodes** | Same seeds queried repeatedly |
| **Combine with text** | Graph alone misses content relevance |

### Common Pitfalls

| Pitfall | Symptom | Solution |
|---------|---------|----------|
| **Ignoring edge direction** | Wrong callers/callees | Explicit `source`/`target` |
| **Unbounded recursion** | Query hangs | Add depth limit |
| **Dense node explosion** | `utils.js` dominates results | Dampen high-degree nodes |
| **Stale graph** | Results don't match code | Incremental updates |
| **Wrong α for PPR** | Too local or too global | Tune per use case |

### Edge Type Semantics

| Edge Type | Direction | Weight Suggestion |
|-----------|-----------|-------------------|
| `calls` | caller → callee | 1.0 (unweighted) |
| `imports` | importer → imported | 1.0 |
| `inherits` | child → parent | 0.5 (strong relationship) |
| `implements` | class → interface | 0.5 |
| `references` | user → used | 1.0 |
| `tests` | test → tested | 0.8 |

---

## References

### Core Algorithm Papers

| Paper | Year | Topic |
|-------|------|-------|
| [Personalized PageRank with Node-dependent Restart](https://arxiv.org/abs/1408.0719) | 2014 | PPR extensions |
| [DeepWalk: Online Learning of Social Representations](https://arxiv.org/abs/1403.6652) | 2014 | Graph embeddings |
| [node2vec: Scalable Feature Learning for Networks](https://arxiv.org/abs/1607.00653) | 2016 | Biased walks |
| [Network Embedding as Matrix Factorization](https://arxiv.org/abs/1710.02971) | 2017 | Unified framework |

### DuckDB Resources

| Resource | Description |
|----------|-------------|
| [USING KEY in Recursive CTEs](https://duckdb.org/2025/05/23/using-key) | SIGMOD 2025 optimization |
| [DuckPGQ Documentation](https://duckpgq.org/) | SQL/PGQ extension |
| [Graph Queries in DuckDB](https://duckdb.org/docs/stable/guides/sql_features/graph_queries) | Official guide |

### Keyword Search over Graphs

| Paper | Venue | Focus |
|-------|-------|-------|
| BANKS | VLDB 2002 | Keyword search + browsing |
| DISCOVER | SIGMOD 2002 | Keyword search in RDBMS |
| STAR | ICDE 2009 | Steiner tree approximation |

### Code-Specific Graph Analysis

| Resource | Description |
|----------|-------------|
| [Code Property Graph](https://docs.joern.io/code-property-graph/) | Unified code representation |
| [Program Dependence Graph](https://en.wikipedia.org/wiki/Program_dependence_graph) | Control + data flow |

---

*Graph ranking turns "what's near this?" into precise answers—the structure of code is as important as its content.*
