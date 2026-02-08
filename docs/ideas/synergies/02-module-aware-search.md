# Synergy 2: Module-Aware Search

> Spectral clustering discovers modules + PPR respects boundaries = Architecturally-informed search

## Overview

Repository content isn't a flat collection of files—it's organized into **modules**, **layers**, **sections**, and **components**. This applies to:

- **Code**: packages, namespaces, services, layers
- **Documentation**: sections, guides, topics, audiences
- **Configuration**: environments, services, feature areas
- **Schemas**: domains, bounded contexts, API versions

This synergy makes RepoQL aware of that structure:

1. **Spectral clustering** automatically discovers boundaries from the relationship graph (imports, links, references)
2. **PPR** uses those boundaries to expand search within relevant clusters first

The result: searches that respect organization, not just text similarity.

## The Problem

Without module awareness, PPR expansion can wander:

```
Query: "authentication"
Search hit: AuthService.cs (in Auth module)

Naive PPR expansion:
  AuthService.cs
    → UserRepository.cs (calls it)
        → DatabaseConnection.cs (calls it)
            → LoggingService.cs (calls it)
                → MetricsCollector.cs (calls it)
                    → ... (now we're in infrastructure)

Result: Context polluted with unrelated infrastructure code
```

## The Solution

```
┌─────────────────────────────────────────────────────────────────┐
│                Module-Aware Expansion                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Discovered Modules (via spectral clustering):                  │
│                                                                  │
│   ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│   │   Auth Module   │  │   User Module   │  │  Infra Module   │ │
│   │                 │  │                 │  │                 │ │
│   │  AuthService    │  │  UserService    │  │  DbConnection   │ │
│   │  JwtValidator   │  │  UserRepo       │  │  Logging        │ │
│   │  AuthMiddleware │  │  UserController │  │  Metrics        │ │
│   │  AuthConfig     │  │  UserDTO        │  │  Cache          │ │
│   │                 │  │                 │  │                 │ │
│   └────────┬────────┘  └────────┬────────┘  └─────────────────┘ │
│            │                    │                                │
│            └──────────┬─────────┘                                │
│                       │                                          │
│              Cross-module edges                                  │
│              (lower weight in PPR)                               │
│                                                                  │
│   Query: "authentication"                                        │
│   Search hit: AuthService.cs (Auth module)                       │
│                                                                  │
│   Module-aware PPR:                                              │
│     1. Expand within Auth module first (weight 1.0)             │
│        → JwtValidator, AuthMiddleware, AuthConfig               │
│     2. Cross to related modules (weight 0.3)                    │
│        → UserService (auth checks users)                        │
│     3. Minimal infra expansion (weight 0.1)                     │
│        → Only if directly called by auth code                   │
│                                                                  │
│   Result: Context stays focused on authentication domain         │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## The Components

### Component 1: Spectral Clustering

**What**: Uses the graph Laplacian's eigenvectors to partition nodes into clusters where intra-cluster edges >> inter-cluster edges.

**Why it works**: The Fiedler vector (2nd smallest eigenvector) naturally separates weakly-connected graph regions. Extending to k eigenvectors gives k clusters.

**Research**: [SpectralGraphTheory.md](../../research/algorithms/SpectralGraphTheory.md) §4 (Spectral Clustering)

```
Algorithm:
1. Build adjacency matrix A from import/call edges
2. Compute normalized Laplacian L = I - D^(-1/2) A D^(-1/2)
3. Find k smallest eigenvectors of L
4. Embed nodes as rows of eigenvector matrix
5. Run k-means on embedded nodes
6. Cluster assignments = module memberships
```

### Component 2: Module-Weighted PPR

**What**: Standard PPR, but edge weights depend on whether the edge crosses module boundaries.

**Research**: [GraphRanking.md](../../research/algorithms/GraphRanking.md) §2 (PPR with edge weights)

```
Edge weight modification:
  w(e) = base_weight * module_factor

Where:
  module_factor = 1.0  if same module
                  0.3  if related modules
                  0.1  if unrelated modules
```

## How They Multiply

| Spectral Alone | PPR Alone | Spectral + PPR |
|----------------|-----------|----------------|
| Knows modules exist | Expands blindly | Expands intelligently |
| Static visualization | No architecture awareness | Architecture-guided expansion |
| Can't guide search | Wanders into unrelated code | Stays in relevant modules |

### The Compound Effect

```
Query: "authentication"

PPR without modules:
  Auth code: 40% of context
  User code: 25% of context
  Infra code: 35% of context  ← wasted

PPR with modules:
  Auth code: 70% of context  ← focused
  User code: 25% of context  ← related
  Infra code: 5% of context  ← minimal
```

## Implementation

### Step 1: Compute Modules (Index Time)

```sql
-- Compute spectral clustering on graph
-- (Simplified: assumes eigenvector UDF exists)

-- Build adjacency
CREATE TABLE code_adjacency AS
SELECT source_uri, target_uri, 1.0 as weight
FROM edge
WHERE type IN ('imports', 'calls', 'implements');

-- Compute Laplacian eigenvectors (via UDF)
CREATE TABLE spectral_embedding AS
SELECT
    uri,
    spectral_eigenvectors(
        (SELECT array_agg(source_uri) FROM code_adjacency),
        (SELECT array_agg(target_uri) FROM code_adjacency),
        (SELECT array_agg(weight) FROM code_adjacency),
        k := 10  -- number of eigenvectors
    ) as embedding
FROM node;

-- Cluster with k-means
CREATE TABLE node_modules AS
SELECT
    uri,
    kmeans_cluster(embedding, k := 8) as module_id  -- 8 modules
FROM spectral_embedding;

-- Add readable names (optional: based on common terms in module)
CREATE TABLE module_names AS
SELECT
    module_id,
    most_common_terms(module_id) as suggested_name
FROM node_modules
GROUP BY module_id;
```

### Step 2: Module-Weighted Edges

```sql
-- Add module weights to edges
CREATE TABLE weighted_edges AS
SELECT
    e.source_uri,
    e.target_uri,
    e.type,
    e.weight as base_weight,
    CASE
        WHEN m1.module_id = m2.module_id THEN 1.0      -- same module
        WHEN module_related(m1.module_id, m2.module_id) THEN 0.3  -- related
        ELSE 0.1                                        -- unrelated
    END as module_factor,
    e.weight * module_factor as adjusted_weight
FROM edge e
JOIN node_modules m1 ON e.source_uri = m1.uri
JOIN node_modules m2 ON e.target_uri = m2.uri;
```

### Step 3: Module-Aware PPR

```sql
-- PPR using module-weighted edges
CREATE MACRO module_aware_ppr(seed_uris, alpha, max_iter, top_k) AS (
    WITH RECURSIVE ppr (uri, score, iter) AS (
        SELECT uri, 1.0 / array_length(seed_uris), 0
        FROM unnest(seed_uris) as t(uri)

        UNION ALL

        SELECT
            we.target_uri as uri,
            SUM(
                alpha * (CASE WHEN we.target_uri = ANY(seed_uris)
                         THEN 1.0/array_length(seed_uris) ELSE 0 END)
                + (1-alpha) * (p.score * we.adjusted_weight / NULLIF(out_sum.total, 0))
            ) as score,
            p.iter + 1
        FROM ppr p
        JOIN weighted_edges we ON p.uri = we.source_uri
        JOIN (
            SELECT source_uri, SUM(adjusted_weight) as total
            FROM weighted_edges
            GROUP BY source_uri
        ) out_sum ON we.source_uri = out_sum.source_uri
        WHERE p.iter < max_iter
        GROUP BY we.target_uri
    ) USING KEY (uri)

    SELECT uri, score
    FROM ppr
    ORDER BY score DESC
    LIMIT top_k
);
```

### Step 4: Module-Scoped Search

```sql
-- Search within a specific module
CREATE MACRO search_in_module(query, module_id, k) AS (
    SELECT s.uri, s.score
    FROM search(query, k := k * 3) s
    JOIN node_modules m ON s.uri = m.uri
    WHERE m.module_id = module_id
    ORDER BY s.score DESC
    LIMIT k
);

-- Auto-detect relevant module from query
CREATE MACRO search_module_aware(query, k) AS (
    WITH initial_hits AS (
        SELECT uri, score FROM search(query, k := 10)
    ),
    dominant_module AS (
        SELECT m.module_id, COUNT(*) as cnt
        FROM initial_hits h
        JOIN node_modules m ON h.uri = m.uri
        GROUP BY m.module_id
        ORDER BY cnt DESC
        LIMIT 1
    )
    SELECT s.uri, s.score
    FROM search(query, k := k * 2) s
    JOIN node_modules m ON s.uri = m.uri
    WHERE m.module_id = (SELECT module_id FROM dominant_module)
       OR s.score > 0.8  -- keep high-relevance cross-module hits
    ORDER BY s.score DESC
    LIMIT k
);
```

## Integration with Explore

```sql
-- Explore with module context
SELECT
    m.module_id,
    m.suggested_name as module_name,
    COUNT(*) as file_count,
    array_agg(n.uri ORDER BY c.pagerank DESC LIMIT 5) as top_files
FROM node_modules m
JOIN node n ON m.uri = n.uri
JOIN node_centrality c ON n.uri = c.uri
GROUP BY m.module_id, m.suggested_name;
```

Output:
```
module_id | module_name    | file_count | top_files
----------+----------------+------------+------------------------------------------
1         | authentication | 12         | [AuthService, JwtValidator, AuthConfig]
2         | user_management| 18         | [UserService, UserRepo, UserController]
3         | api_layer      | 25         | [ApiController, RouteHandler, Middleware]
4         | data_access    | 15         | [DbContext, Repository, QueryBuilder]
...
```

## Expected Impact

### Quantitative

| Metric | Without Modules | With Modules | Change |
|--------|-----------------|--------------|--------|
| Relevant module files in context | 45% | 75% | +67% |
| Unrelated infra files | 30% | 8% | -73% |
| Cross-module noise | High | Low | -80% |

### Qualitative

**Before**: "Show me authentication" returns auth code mixed with logging, metrics, and database utilities.

**After**: "Show me authentication" returns auth code, with related user code, minimal infrastructure.

## Visualization Opportunity

Modules enable architecture visualization:

```
┌──────────────────────────────────────────────────────────────┐
│  Repository Architecture (auto-discovered)                    │
├──────────────────────────────────────────────────────────────┤
│                                                               │
│    ┌─────────┐         ┌─────────┐         ┌─────────┐       │
│    │   API   │────────▶│  Auth   │────────▶│  User   │       │
│    │  (25)   │         │  (12)   │         │  (18)   │       │
│    └────┬────┘         └────┬────┘         └────┬────┘       │
│         │                   │                   │             │
│         └───────────────────┼───────────────────┘             │
│                             │                                 │
│                             ▼                                 │
│                       ┌─────────┐                             │
│                       │  Data   │                             │
│                       │  (15)   │                             │
│                       └─────────┘                             │
│                                                               │
│  Numbers in parentheses = file count                         │
│  Arrows = dominant dependency direction                      │
│                                                               │
└──────────────────────────────────────────────────────────────┘
```

## Complexity and Performance

| Operation | Complexity | When |
|-----------|------------|------|
| Spectral clustering | O(n·k·m) | Index build (one-time) |
| Module lookup | O(1) | Query time |
| Weighted PPR | Same as regular PPR | Query time |

**Storage**: ~4 bytes per node (module_id as int).

## When to Recompute Modules

| Trigger | Action |
|---------|--------|
| >20% of files changed | Full recompute |
| New files added | Assign to nearest module |
| Major refactor | Full recompute |
| Periodic | Weekly recompute |

## Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| Graph edges | Exists | `edge` table |
| Eigensolver | Needed | UDF or external library |
| K-means | Needed | UDF or DuckDB ML extension |
| PPR | Synergy 1 | [01-intelligent-context-selection.md](01-intelligent-context-selection.md) |

## Open Questions

1. **Number of modules (k)**: Use eigengap heuristic? User-configurable?
2. **Module relationships**: Binary (related/not) or continuous similarity?
3. **Hierarchical modules**: Nested structure (module → submodule)?
4. **Module names**: Auto-generate from common terms or require labels?

## References

- [SpectralGraphTheory.md](../../research/algorithms/SpectralGraphTheory.md) - Clustering theory
- [GraphRanking.md](../../research/algorithms/GraphRanking.md) - PPR with edge weights
- [Idea 006](../006-spectral-module-detection.md) - Standalone spectral clustering details

---

*This synergy makes RepoQL architecture-aware, not just content-aware.*
