# Spectral Clustering for Module Detection

> Automatically discover code modules using graph Laplacian eigenvectors

## Problem

Large codebases have implicit module structure that isn't always reflected in folder organization:
- Tightly coupled files that should be grouped
- Cross-cutting concerns that span directories
- "God modules" that should be split

Understanding module structure helps:
- Scope searches to relevant clusters
- Identify architectural boundaries
- Suggest refactoring opportunities

## Proposed Solution

Apply **spectral clustering** to the code dependency graph to discover natural module boundaries.

```
┌─────────────────────────────────────────────────────────────────┐
│              Spectral Module Detection                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Code Graph (imports/calls)                                     │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ Build Graph  │  A ──── B                                     │
│   │ Laplacian L  │  │ \  / │                                     │
│   │              │  │  \/  │     L = D - A                       │
│   └──────────────┘  │  /\  │     (degree - adjacency)            │
│       │             C ──── D                                     │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ Compute k    │  λ₁ = 0, λ₂ = 0.5, λ₃ = 1.2, ...            │
│   │ smallest     │  v₁, v₂, v₃, ...                             │
│   │ eigenvectors │  (Fiedler vector v₂ splits graph)            │
│   └──────────────┘                                               │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ k-means on   │  Cluster 1: {A, B}                           │
│   │ eigenvector  │  Cluster 2: {C, D}                           │
│   │ embeddings   │                                               │
│   └──────────────┘                                               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Why Spectral Clustering?

The **Fiedler vector** (second smallest eigenvector of Laplacian) naturally identifies graph cuts:
- Entries with same sign → same cluster
- Magnitude indicates distance from cut
- Cheeger inequality guarantees cut quality

```
Fiedler vector for code graph:

  AuthService.cs    [+0.45]  ─┐
  AuthMiddleware.cs [+0.42]   ├─► Auth Module
  JwtValidator.cs   [+0.38]  ─┘

  UserRepo.cs       [-0.41]  ─┐
  UserService.cs    [-0.44]   ├─► User Module
  UserController.cs [-0.39]  ─┘
```

## Implementation

### Step 1: Build Adjacency Matrix

```sql
-- Create symmetric adjacency matrix from edges
CREATE TABLE adjacency AS
WITH directed AS (
    SELECT source_uri, target_uri, 1.0 as weight
    FROM edge
    WHERE type IN ('imports', 'calls')  -- relevant edge types
),
symmetric AS (
    SELECT source_uri, target_uri, weight FROM directed
    UNION
    SELECT target_uri, source_uri, weight FROM directed
)
SELECT source_uri, target_uri, MAX(weight) as weight
FROM symmetric
GROUP BY source_uri, target_uri;
```

### Step 2: Compute Laplacian

```sql
-- Degree matrix (diagonal)
CREATE TABLE degree AS
SELECT source_uri as uri, SUM(weight) as degree
FROM adjacency
GROUP BY source_uri;

-- Normalized Laplacian: L_sym = I - D^(-1/2) A D^(-1/2)
-- For spectral clustering, we need eigenvectors of this
```

### Step 3: Eigenvector Computation (via UDF)

Since DuckDB doesn't have native eigensolvers, implement as UDF:

```csharp
[UdfScalar("spectral_embed")]
public static double[] SpectralEmbed(
    string[] nodes,
    string[] sources,
    string[] targets,
    double[] weights,
    int k)
{
    // Build sparse Laplacian matrix
    var L = BuildNormalizedLaplacian(nodes, sources, targets, weights);

    // Compute k smallest eigenvectors (using Lanczos or ARPACK)
    var (eigenvalues, eigenvectors) = ComputeSmallestEigenpairs(L, k);

    // Return flattened embedding matrix (n × k)
    return eigenvectors.Flatten();
}
```

### Step 4: Clustering

```sql
-- After computing spectral embedding
CREATE TABLE node_clusters AS
WITH embeddings AS (
    SELECT
        uri,
        spectral_embed(...) as embedding  -- k-dimensional vector
    FROM nodes
)
SELECT
    uri,
    kmeans_cluster(embedding, k := 5) as cluster_id  -- k-means on embeddings
FROM embeddings;
```

## Practical Alternative: Power Iteration for Fiedler Vector

For simple 2-way partitioning without full eigendecomposition:

```sql
-- Approximate Fiedler vector via power iteration
WITH RECURSIVE power_iter (uri, value, iter) AS (
    -- Initialize with random values
    SELECT uri, random() - 0.5, 0
    FROM node

    UNION ALL

    -- Iterate: v = L * v, then orthogonalize against all-ones
    SELECT
        n.uri,
        -- Laplacian multiplication + orthogonalization
        COALESCE(SUM(
            CASE WHEN e.target_uri = n.uri
                 THEN -p.value / d.degree
                 ELSE 0 END
        ), 0) + p.value - AVG(p.value) OVER (),
        p.iter + 1
    FROM power_iter p
    JOIN node n ON TRUE
    LEFT JOIN edge e ON e.source_uri = p.uri
    LEFT JOIN degree d ON d.uri = p.uri
    WHERE p.iter < 50
    GROUP BY n.uri, p.value, p.iter
) USING KEY (uri)

SELECT uri, value as fiedler_value
FROM power_iter
WHERE iter = (SELECT MAX(iter) FROM power_iter)
ORDER BY fiedler_value;

-- Partition by sign of Fiedler value
SELECT
    uri,
    CASE WHEN fiedler_value >= 0 THEN 'cluster_1' ELSE 'cluster_2' END as cluster
FROM fiedler_result;
```

## Use Cases

### 1. Scope Search to Module

```sql
-- Find which module a file belongs to
SELECT cluster_id FROM node_clusters WHERE uri = 'file:///src/auth/service.cs';

-- Search only within that module
SELECT * FROM search('validation', scope := (
    SELECT array_agg(uri) FROM node_clusters WHERE cluster_id = 3
));
```

### 2. Module Overview in XRay

```sql
-- Show module structure in xray Explore
SELECT
    cluster_id,
    COUNT(*) as file_count,
    array_agg(uri ORDER BY pagerank DESC LIMIT 3) as top_files
FROM node_clusters
JOIN node_centrality USING (uri)
GROUP BY cluster_id;
```

### 3. Cross-Module Dependencies

```sql
-- Find edges that cross module boundaries
SELECT
    e.source_uri,
    e.target_uri,
    c1.cluster_id as from_cluster,
    c2.cluster_id as to_cluster
FROM edge e
JOIN node_clusters c1 ON e.source_uri = c1.uri
JOIN node_clusters c2 ON e.target_uri = c2.uri
WHERE c1.cluster_id != c2.cluster_id;
```

## Expected Benefits

| Benefit | Description |
|---------|-------------|
| Automatic module discovery | No manual tagging needed |
| Search scoping | Limit results to relevant module |
| Architecture visualization | Show cluster structure |
| Refactoring hints | Identify tightly coupled clusters |

## Complexity

- **Eigendecomposition**: O(n³) naive, O(n·k·m) with Lanczos (m = edges)
- **Storage**: O(n·k) for k-dimensional embeddings
- **Update**: Recompute on significant graph changes (batch, not per-file)

## When to Recompute

| Trigger | Action |
|---------|--------|
| >10% of files changed | Full recompute |
| Single file added | Approximate update |
| Import structure changed | Recompute affected region |

## Open Questions

1. How many clusters (k)? Use eigengap heuristic?
2. Hierarchical clustering for nested modules?
3. Cache module assignments vs recompute on query?

## References

- [SpectralGraphTheory.md](../research/algorithms/SpectralGraphTheory.md) - Full theory
- Shi & Malik (2000) - Normalized cuts
- Ng, Jordan, Weiss (2002) - Spectral clustering algorithm
- von Luxburg (2007) - Tutorial on spectral clustering
