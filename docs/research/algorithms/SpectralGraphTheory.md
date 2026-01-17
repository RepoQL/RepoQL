# Spectral Graph Theory

> Mathematical foundations for analyzing graphs through eigenvalues and eigenvectors of associated matrices

## Table of Contents

1. [Overview](#overview)
2. [Graph Laplacians](#graph-laplacians)
   - [Unnormalized Laplacian](#unnormalized-laplacian)
   - [Normalized Laplacians](#normalized-laplacians)
   - [Properties and Intuition](#properties-and-intuition)
3. [Eigenvalues and Eigenvectors](#eigenvalues-and-eigenvectors)
   - [Spectral Decomposition](#spectral-decomposition)
   - [Fiedler Vector and Algebraic Connectivity](#fiedler-vector-and-algebraic-connectivity)
   - [Cheeger Inequality](#cheeger-inequality)
4. [Spectral Clustering](#spectral-clustering)
   - [Graph Cuts](#graph-cuts)
   - [k-way Spectral Clustering Algorithm](#k-way-spectral-clustering-algorithm)
   - [Connection to k-means](#connection-to-k-means)
5. [Random Walks and Laplacians](#random-walks-and-laplacians)
   - [Random Walk Interpretation](#random-walk-interpretation)
   - [Hitting Times and Commute Distances](#hitting-times-and-commute-distances)
   - [PageRank as Spectral Problem](#pagerank-as-spectral-problem)
6. [Graph Signal Processing](#graph-signal-processing)
   - [Graph Fourier Transform](#graph-fourier-transform)
   - [Graph Wavelets and Filtering](#graph-wavelets-and-filtering)
7. [Low-rank Approximations](#low-rank-approximations)
   - [Truncated Eigendecomposition](#truncated-eigendecomposition)
   - [Nystrom Approximation](#nystrom-approximation)
8. [Applications to Code Graphs](#applications-to-code-graphs)
   - [Module Detection via Spectral Clustering](#module-detection-via-spectral-clustering)
   - [Anomaly Detection](#anomaly-detection)
   - [Laplacian Eigenmaps for Code Embedding](#laplacian-eigenmaps-for-code-embedding)
9. [Computational Considerations](#computational-considerations)
   - [Sparse Eigensolvers](#sparse-eigensolvers)
   - [Approximation for Large Graphs](#approximation-for-large-graphs)
10. [References](#references)

---

## Overview

Spectral graph theory studies graphs through the eigenvalues and eigenvectors of matrices associated with them. For code analysis, this provides powerful tools for:

- **Clustering**: Automatically detecting modules and communities in code
- **Partitioning**: Finding natural boundaries for code organization
- **Embedding**: Creating low-dimensional representations of code structure
- **Anomaly detection**: Identifying unusual structural patterns

```
+-----------------------------------------------------------------------+
|                    Spectral Analysis Pipeline                          |
+-----------------------------------------------------------------------+
|                                                                        |
|   Code Graph              Laplacian             Eigendecomposition     |
|                                                                        |
|   +---+                   +-------------+       +------------------+   |
|   | A |---+               | L = D - A   |       | L = U Lambda U^T |   |
|   +---+   |               +-------------+       +------------------+   |
|     |     v                     |                      |               |
|     |   +---+                   v                      v               |
|     +-->| B |             Captures graph         Eigenvalues: global   |
|         +---+             structure in           structure (spectrum)  |
|           |               matrix form            Eigenvectors: node    |
|           v                                      embeddings            |
|         +---+                                                          |
|         | C |                                                          |
|         +---+                                                          |
|                                                                        |
+-----------------------------------------------------------------------+
```

### Why Spectral Methods for Code?

| Property | Spectral Advantage | Code Application |
|----------|-------------------|------------------|
| **Global structure** | Eigenvalues encode connectivity | Detect isolated modules |
| **Smooth clustering** | Eigenvectors are continuous | Fuzzy module boundaries |
| **Mathematical rigor** | Proven approximation bounds | Reliable partitioning |
| **Dimensionality reduction** | Low-rank approximation | Code embeddings |

---

## Graph Laplacians

The Laplacian matrix is the central object in spectral graph theory. It encodes the graph structure in a matrix that has elegant mathematical properties.

### Unnormalized Laplacian

For an undirected graph G = (V, E) with n vertices:

**Definition**: The unnormalized Laplacian is:

```
L = D - A

Where:
  D = diagonal matrix of vertex degrees
  A = adjacency matrix

         +----+----+----+----+
         | d1 |  0 |  0 |  0 |       +----+----+----+----+
         +----+----+----+----+       | 0  | a12| a13| a14|
    D =  |  0 | d2 |  0 |  0 |   A = | a21| 0  | a23| a24|
         +----+----+----+----+       | a31| a32| 0  | a34|
         |  0 |  0 | d3 |  0 |       | a41| a42| a43| 0  |
         +----+----+----+----+       +----+----+----+----+
         |  0 |  0 |  0 | d4 |

Entry-wise:
         / d_i           if i = j
L_ij =  |  -1            if i != j and (i,j) in E
         \ 0             otherwise
```

**Example**: Path graph with 4 vertices

```
Graph:  1 --- 2 --- 3 --- 4

        +----+----+----+----+       +----+----+----+----+
        | 1  | -1 |  0 |  0 |       | 1  |  0 |  0 |  0 |
        +----+----+----+----+       +----+----+----+----+
   L =  | -1 |  2 | -1 |  0 |   D = | 0  |  2 |  0 |  0 |
        +----+----+----+----+       +----+----+----+----+
        |  0 | -1 |  2 | -1 |       | 0  |  0 |  2 |  0 |
        +----+----+----+----+       +----+----+----+----+
        |  0 |  0 | -1 |  1 |       | 0  |  0 |  0 |  1 |
        +----+----+----+----+       +----+----+----+----+
```

**Quadratic Form (Dirichlet Energy)**:

For any vector x in R^n:

```
x^T L x = sum over edges (i,j) of (x_i - x_j)^2

This measures the "smoothness" of x over the graph:
  - Small when x varies slowly across edges
  - Large when neighboring nodes have different values
```

### Normalized Laplacians

Two common normalizations exist, each with different properties:

**Symmetric Normalized Laplacian** (L_sym):

```
L_sym = D^(-1/2) L D^(-1/2) = I - D^(-1/2) A D^(-1/2)

Entry-wise:
            / 1                        if i = j and d_i != 0
L_sym_ij = |  -1/sqrt(d_i * d_j)       if i != j and (i,j) in E
            \ 0                        otherwise
```

**Random Walk Laplacian** (L_rw):

```
L_rw = D^(-1) L = I - D^(-1) A = I - P

Where P = D^(-1) A is the random walk transition matrix

Entry-wise:
            / 1              if i = j
L_rw_ij =  |  -1/d_i         if i != j and (i,j) in E
            \ 0              otherwise
```

### Properties and Intuition

| Property | L (unnormalized) | L_sym | L_rw |
|----------|------------------|-------|------|
| **Symmetry** | Yes | Yes | No (but similar to L_sym) |
| **Eigenvalues** | 0 = lambda_1 <= ... <= lambda_n | 0 = mu_1 <= ... <= mu_n <= 2 | Same as L_sym |
| **Null space** | span{1} for connected | span{D^(1/2) 1} | span{1} |
| **Interpretation** | Absolute smoothness | Relative smoothness | Random walk |

**Key Property**: The number of zero eigenvalues equals the number of connected components.

```
Connected Graph:                Disconnected Graph:
+---+     +---+                 +---+     +---+   +---+     +---+
| A |-----| B |                 | A |-----| B |   | E |-----| F |
+---+     +---+                 +---+     +---+   +---+     +---+
  |         |                     |         |
  |         |
+---+     +---+                 +---+     +---+
| C |-----| D |                 | C |-----| D |
+---+     +---+                 +---+     +---+

lambda_1 = 0                    lambda_1 = lambda_2 = 0
(one zero eigenvalue)           (two zero eigenvalues)
```

---

## Eigenvalues and Eigenvectors

### Spectral Decomposition

Since L is real and symmetric, it admits a spectral decomposition:

```
L = U Lambda U^T

Where:
  U = [u_1 | u_2 | ... | u_n]  (orthonormal eigenvectors)
  Lambda = diag(lambda_1, lambda_2, ..., lambda_n)

Ordering: 0 = lambda_1 <= lambda_2 <= ... <= lambda_n

The first eigenvector u_1 is constant (for connected graphs):
  u_1 = (1/sqrt(n)) * [1, 1, ..., 1]^T
```

**Spectral Gap**: The difference lambda_2 - lambda_1 = lambda_2 measures how "well-connected" the graph is.

```
+-----------------------------------------------------------------------+
|                    Spectral Gap Interpretation                         |
+-----------------------------------------------------------------------+
|                                                                        |
|   Large lambda_2 (well-connected):    Small lambda_2 (sparse cut):     |
|                                                                        |
|       +---+---+---+                       +---+     +---+              |
|       | A-+-B |   |                       | A |-----| B |              |
|       +---+ | +---+                       +---+     +---+              |
|         | +-+-+ |                           |         |               |
|         |   |   |                           |    .    |  (weak link)  |
|       +---+ | +---+                       +---+     +---+              |
|       | C-+-+-D |                         | C |-----| D |              |
|       +---+---+---+                       +---+     +---+              |
|                                                                        |
|   Hard to disconnect                     Easy to disconnect            |
|   (many paths between nodes)             (bottleneck exists)           |
|                                                                        |
+-----------------------------------------------------------------------+
```

### Fiedler Vector and Algebraic Connectivity

The second eigenvalue lambda_2 and its eigenvector u_2 have special significance:

**Algebraic Connectivity**: lambda_2 is called the algebraic connectivity (Fiedler, 1973)

```
Properties of lambda_2:
  - lambda_2 > 0 if and only if G is connected
  - Larger lambda_2 implies better connectivity
  - lambda_2 <= vertex connectivity
  - lambda_2 <= edge connectivity
```

**Fiedler Vector**: u_2 is called the Fiedler vector

```
Fiedler's Theorem:
For a connected graph G with Fiedler vector u_2:
  - Let V+ = {i : u_2(i) >= 0}
  - Let V- = {i : u_2(i) < 0}
  - Both induced subgraphs G[V+] and G[V-] are connected

This gives a natural bipartition of the graph!
```

**Example**: Fiedler vector for graph bisection

```
Graph with bottleneck:

    A---B---C
        |
        D  (weak link)
        |
    E---F---G

Fiedler vector might be approximately:
  u_2 = [0.4, 0.35, 0.4, 0.0, -0.4, -0.35, -0.4]^T
         A    B     C    D    E     F      G

Natural cut: {A, B, C, D} vs {E, F, G}
```

### Cheeger Inequality

The Cheeger inequality is arguably the most important result in spectral graph theory. It connects the combinatorial notion of graph cuts to the algebraic notion of eigenvalues.

**Cheeger Constant (Conductance)**:

```
h(G) = min over all cuts S with |S| <= n/2 of:

         |E(S, V\S)|
  h(S) = -----------
          min(vol(S), vol(V\S))

Where:
  E(S, V\S) = edges between S and its complement
  vol(S) = sum of degrees of vertices in S
```

**Cheeger Inequality**:

```
lambda_2 / 2  <=  h(G)  <=  sqrt(2 * lambda_2)

Left inequality: spectral gap lower bounds conductance
Right inequality: conductance upper bounds spectral gap (tight up to sqrt)
```

**Implications**:

| lambda_2 | h(G) | Graph Structure |
|----------|------|-----------------|
| Close to 0 | Close to 0 | Near-disconnected, sparse cut exists |
| Large | Large | Well-connected, expander-like |
| 0 | 0 | Disconnected |

**Algorithmic Significance**: The Fiedler vector provides an O(sqrt(log n))-approximation to the sparsest cut problem, which is NP-hard to solve exactly.

---

## Spectral Clustering

Spectral clustering leverages eigenvectors to partition graphs into clusters. It often outperforms traditional methods on non-convex cluster shapes.

### Graph Cuts

**RatioCut**:

```
RatioCut(A_1, ..., A_k) = sum from i=1 to k of:

    |E(A_i, complement(A_i))|
    -------------------------
            |A_i|

Minimizes cut edges normalized by cluster size (cardinality)
```

**Normalized Cut (NCut)**:

```
NCut(A_1, ..., A_k) = sum from i=1 to k of:

    |E(A_i, complement(A_i))|
    -------------------------
          vol(A_i)

Minimizes cut edges normalized by cluster volume (sum of degrees)
```

**Relationship to Laplacians**:

| Cut Objective | Related Laplacian | Relaxation |
|---------------|-------------------|------------|
| RatioCut | Unnormalized L | Smallest eigenvectors of L |
| NCut | Normalized L_sym or L_rw | Smallest eigenvectors of L_sym |

### k-way Spectral Clustering Algorithm

```
+-----------------------------------------------------------------------+
|              Spectral Clustering Algorithm (NCut)                      |
+-----------------------------------------------------------------------+
|                                                                        |
|   Input: Similarity matrix W, number of clusters k                     |
|                                                                        |
|   1. Construct Graph Laplacian                                         |
|      D = diag(W * 1)         (degree matrix)                          |
|      L = D - W               (unnormalized Laplacian)                 |
|      L_sym = D^(-1/2) L D^(-1/2)                                      |
|                                                                        |
|   2. Compute Eigenvectors                                              |
|      Find first k eigenvectors u_1, ..., u_k of L_sym                 |
|      Form matrix U = [u_1 | u_2 | ... | u_k] (n x k)                  |
|                                                                        |
|   3. Normalize Rows                                                    |
|      For each row i of U, normalize to unit length:                   |
|      T_ij = U_ij / sqrt(sum_j U_ij^2)                                 |
|                                                                        |
|   4. Cluster in Spectral Space                                         |
|      Treat rows of T as points in R^k                                 |
|      Apply k-means to get clusters C_1, ..., C_k                      |
|                                                                        |
|   5. Return Partition                                                  |
|      Assign original node i to cluster j if row i in C_j              |
|                                                                        |
+-----------------------------------------------------------------------+
```

**Visual Intuition**:

```
Original Space (non-convex clusters):     Spectral Space (k=2):

       ***  ooo                            *
      *   ** o                           *   *
     *      *o                              o
      *    * oo                           o   o
       ****  ooo                            o

  Hard for k-means                       Easy for k-means
  (clusters overlap)                     (linearly separable)
```

### Connection to k-means

The spectral clustering algorithm can be understood as:

1. **Embedding**: Map nodes to R^k using bottom k eigenvectors
2. **Clustering**: Run k-means in the embedded space

**Why this works**:

```
The bottom k eigenvectors minimize:

  Tr(U^T L U)  subject to  U^T U = I

This is a relaxation of the discrete cut problem.
The eigenvectors naturally separate clusters because:
  - Within-cluster: eigenvector values are similar
  - Between-clusters: eigenvector values differ
```

**SQL Sketch for Spectral Embedding**:

```sql
-- Compute spectral embedding (conceptual - requires eigendecomposition UDF)
WITH degree AS (
    SELECT source as node, SUM(weight) as deg
    FROM edge
    GROUP BY source
),
laplacian_entries AS (
    SELECT
        CASE WHEN e.source = e.target THEN d.deg ELSE -e.weight END as value,
        e.source as row_idx,
        e.target as col_idx
    FROM edge e
    JOIN degree d ON e.source = d.node
),
-- Assume eigendecomposition UDF exists
embedding AS (
    SELECT node, eigenvector_1, eigenvector_2, eigenvector_3
    FROM spectral_embedding(laplacian_entries, k := 3)
)
SELECT * FROM embedding;
```

---

## Random Walks and Laplacians

The connection between random walks and graph Laplacians provides both computational tools and intuitive understanding.

### Random Walk Interpretation

The random walk transition matrix P relates to the normalized Laplacian:

```
P = D^(-1) A

P_ij = probability of stepping from i to j
     = A_ij / d_i  (uniform over neighbors)

Random Walk Laplacian:
L_rw = I - P

Key relationship:
If Lx = lambda * x  (eigenvector of L)
Then D^(-1/2) x is eigenvector of L_sym with same eigenvalue
```

**Stationary Distribution**:

```
For a connected, non-bipartite graph:
  - Random walk converges to stationary distribution pi
  - pi_i = d_i / (2|E|)  (proportional to degree)
  - pi = D*1 / (1^T D 1)
```

### Hitting Times and Commute Distances

**Hitting Time**: Expected time for random walk from u to reach v

```
H_uv = E[first time to reach v | start at u]

Computed via linear system:
H_uv = 1 + sum over neighbors w of u: P_uw * H_wv
       (with boundary: H_vv = 0)
```

**Commute Time**: Round-trip expected time

```
C_uv = H_uv + H_vu

Connection to Laplacian pseudoinverse L+:
C_uv = vol(G) * (L+_uu + L+_vv - 2*L+_uv)

Where vol(G) = sum of all degrees = 2|E|
```

**Resistance Distance**:

```
R_uv = L+_uu + L+_vv - 2*L+_uv = C_uv / vol(G)

Interpretation: effective resistance if edges are unit resistors
```

**Important Caveat for Large Graphs**:

```
For large random geometric graphs (von Luxburg et al., 2014):

As n -> infinity:
  H_uv -> 1/d_v  (depends only on target degree!)
  C_uv -> 1/d_u + 1/d_v

The commute distance loses global structure information
and only reflects local density (degree).
```

### PageRank as Spectral Problem

PageRank can be viewed through the lens of spectral graph theory:

**PageRank Definition**:

```
pi = alpha * s + (1 - alpha) * P^T * pi

Where:
  alpha = damping factor (typically 0.15)
  s = teleportation distribution (often uniform: s = 1/n)
  P = transition matrix
  pi = PageRank vector
```

**Spectral Interpretation**:

```
Rearranging: (I - (1-alpha) * P^T) * pi = alpha * s

PageRank is the principal eigenvector of the modified matrix:
  G = alpha * s * 1^T + (1 - alpha) * P^T

Properties:
  - G is stochastic (columns sum to 1)
  - PageRank = left eigenvector with eigenvalue 1
  - Perron-Frobenius: unique positive eigenvector
```

**Power Method Convergence**:

```
pi^(t+1) = alpha * s + (1 - alpha) * P^T * pi^(t)

Convergence rate: O((1-alpha)^t)
Typically converges in 50-100 iterations for alpha = 0.15
```

---

## Graph Signal Processing

Graph Signal Processing (GSP) extends classical signal processing to data on graphs using the spectral properties of the Laplacian.

### Graph Fourier Transform

**Classical Fourier Transform**: Decomposes signals into sinusoidal components

**Graph Fourier Transform**: Decomposes graph signals into Laplacian eigenvector components

```
For signal x on graph G with Laplacian L:
  L = U Lambda U^T  (eigendecomposition)

Graph Fourier Transform:
  x_hat = U^T x     (analysis: spatial -> spectral)

Inverse Graph Fourier Transform:
  x = U x_hat       (synthesis: spectral -> spatial)
```

**Frequency Interpretation**:

```
+-----------------------------------------------------------------------+
|                    Graph Frequencies                                   |
+-----------------------------------------------------------------------+
|                                                                        |
|   Low frequency (lambda small):          High frequency (lambda large):|
|                                                                        |
|   Eigenvector is smooth:                 Eigenvector oscillates:       |
|                                                                        |
|   +0.5   +0.5   +0.5   +0.5              +0.5   -0.5   +0.5   -0.5    |
|     o------o------o------o                 o------o------o------o      |
|                                                                        |
|   Adjacent nodes have                    Adjacent nodes have           |
|   similar values                         opposite values               |
|                                                                        |
+-----------------------------------------------------------------------+
```

### Graph Wavelets and Filtering

**Graph Filtering**:

```
Apply filter h(lambda) in spectral domain:

y = h(L) x = U h(Lambda) U^T x

Where h(Lambda) = diag(h(lambda_1), ..., h(lambda_n))

Examples:
  Low-pass: h(lambda) = 1/(1 + lambda)     (smooth the signal)
  High-pass: h(lambda) = lambda/(1+lambda) (detect edges/changes)
  Band-pass: h(lambda) = exp(-(lambda-mu)^2 / sigma^2)
```

**Spectral Graph Wavelets** (Hammond et al., 2011):

```
Wavelet at scale s centered at node n:

psi_s,n = U g(s*Lambda) U^T delta_n

Where:
  g = wavelet generating kernel
  s = scale parameter
  delta_n = indicator vector for node n

Common choice: g(x) = x * exp(-x)  (Mexican hat-like)
```

**Fast Computation via Chebyshev Approximation**:

```
Avoid explicit eigendecomposition by approximating filter:

h(L) x ~= sum from k=0 to K of: c_k T_k(L_tilde) x

Where:
  T_k = Chebyshev polynomial of degree k
  L_tilde = 2L/lambda_max - I  (scaled Laplacian)
  c_k = Chebyshev coefficients of h

Cost: O(K * |E|) instead of O(n^3) for eigendecomposition
```

---

## Low-rank Approximations

For large graphs, computing full eigendecomposition is prohibitive. Low-rank methods provide practical alternatives.

### Truncated Eigendecomposition

**Idea**: Keep only the k smallest (or largest) eigenvalues

```
L ~= U_k Lambda_k U_k^T

Where:
  U_k = [u_1 | ... | u_k]     (n x k matrix)
  Lambda_k = diag(lambda_1, ..., lambda_k)

Storage: O(nk) instead of O(n^2)
```

**Applications**:

| k | Application |
|---|-------------|
| k = 2-10 | Spectral clustering |
| k = 50-100 | Graph embeddings |
| k = O(log n) | Approximation algorithms |

### Nystrom Approximation

The Nystrom method approximates eigenvectors by sampling a subset of nodes.

**Algorithm**:

```
+-----------------------------------------------------------------------+
|                    Nystrom Approximation                               |
+-----------------------------------------------------------------------+
|                                                                        |
|   1. Sample m << n landmark nodes (uniformly or by importance)         |
|                                                                        |
|   2. Compute submatrices:                                              |
|      A = W(landmarks, landmarks)      (m x m)                         |
|      B = W(landmarks, others)         (m x (n-m))                     |
|                                                                        |
|   3. Eigendecompose small matrix:                                      |
|      A = V_A Lambda_A V_A^T                                           |
|                                                                        |
|   4. Approximate full eigenvectors:                                    |
|            +----+           +-----+                                    |
|            | V_A|           |V_A Lambda_A^(-1/2)|                     |
|      U ~=  +----+  V_A  =   +-------------------+                     |
|            |B^T |           |B^T V_A Lambda_A^(-1)|                    |
|            +----+           +---------------------+                    |
|                                                                        |
|   Complexity: O(m^2 n + m^3) instead of O(n^3)                        |
|                                                                        |
+-----------------------------------------------------------------------+
```

**Sampling Strategies**:

| Strategy | Description | Quality |
|----------|-------------|---------|
| Uniform | Random selection | Baseline |
| Leverage score | Sample by diagonal of projection | Better approximation |
| k-DPP | Diversity sampling | Best quality, slower |
| k-core | Graph structure-aware | Good for networks |

**Limitations**:

```
Caution: Nystrom approximation can produce negative entries
in what should be a positive semidefinite matrix.

This affects spectral clustering quality when:
  - Sample size m is too small
  - Sampling misses important structure
  - Graph has complex multi-scale structure
```

---

## Applications to Code Graphs

### Module Detection via Spectral Clustering

Spectral clustering can automatically discover module structure in codebases.

```
+-----------------------------------------------------------------------+
|              Module Detection Pipeline                                 |
+-----------------------------------------------------------------------+
|                                                                        |
|   1. Build similarity graph from code relationships:                   |
|      - Function calls (weight by frequency)                           |
|      - Shared imports                                                  |
|      - Co-change history                                               |
|                                                                        |
|   2. Apply spectral clustering:                                        |
|      - Compute normalized Laplacian                                    |
|      - Extract bottom k eigenvectors                                   |
|      - Cluster in spectral space                                       |
|                                                                        |
|   3. Interpret clusters as modules:                                    |
|      - Sparse inter-cluster edges = good module boundaries            |
|      - Dense intra-cluster edges = cohesive modules                   |
|                                                                        |
+-----------------------------------------------------------------------+

Example result:

   +-----------------+     +------------------+
   |  Auth Module    |     |  Data Module     |
   |                 |     |                  |
   | AuthService     |     | UserRepository   |
   | JwtValidator    |<--->| DataContext      |
   | TokenManager    |     | QueryBuilder     |
   |                 |     |                  |
   +-----------------+     +------------------+
          ^                         |
          |   +-----------------+   |
          +---|  API Module     |---+
              |                 |
              | Controller      |
              | Middleware      |
              | Router          |
              |                 |
              +-----------------+
```

**SQL Sketch**:

```sql
-- Detect modules using spectral methods
WITH call_weights AS (
    SELECT
        source_file,
        target_file,
        COUNT(*) as weight
    FROM function_calls
    GROUP BY source_file, target_file
),
-- Build symmetric similarity
similarity AS (
    SELECT
        LEAST(source_file, target_file) as file_a,
        GREATEST(source_file, target_file) as file_b,
        SUM(weight) as similarity
    FROM call_weights
    GROUP BY file_a, file_b
),
-- Apply spectral clustering (conceptual - requires UDF)
clusters AS (
    SELECT file, cluster_id
    FROM spectral_cluster(
        similarity_edges := similarity,
        k := 5  -- number of modules to detect
    )
)
SELECT
    cluster_id as module_id,
    array_agg(file ORDER BY file) as files,
    COUNT(*) as module_size
FROM clusters
GROUP BY cluster_id
ORDER BY module_size DESC;
```

### Anomaly Detection

Spectral residuals can identify structurally unusual code patterns.

**Approach**:

```
1. Project node features onto low-frequency eigenvectors
2. High reconstruction error = structural anomaly

For node i with feature x_i:
  x_low = sum over k smallest eigenvectors: (u_k^T x) * u_k
  anomaly_score_i = ||x_i - x_low_i||

High score indicates:
  - Unusual connectivity pattern
  - Different from structural neighbors
  - Potential architectural violation
```

**Applications**:

| Anomaly Type | Detection Method |
|--------------|------------------|
| God class | High degree, low clustering coefficient |
| Orphan code | Disconnected in spectral space |
| Circular dependency | Detected by graph cycle analysis |
| Layer violation | Connection to unexpected spectral cluster |

### Laplacian Eigenmaps for Code Embedding

Laplacian Eigenmaps (Belkin & Niyogi, 2003) creates embeddings where graph neighbors are close in the embedding space.

**Objective**:

```
Minimize: sum over edges (i,j): w_ij * ||y_i - y_j||^2

Subject to: Y^T D Y = I  (prevent collapse)

Solution: Y = [u_2 | u_3 | ... | u_{k+1}]
          (skip u_1, the constant eigenvector)
```

**For Code Graphs**:

```
+-----------------------------------------------------------------------+
|              Code Embedding via Laplacian Eigenmaps                    |
+-----------------------------------------------------------------------+
|                                                                        |
|   Input: Code relationship graph (calls, imports, etc.)                |
|                                                                        |
|   1. Construct weighted adjacency:                                     |
|      - Call edge weight = 1/call_frequency                            |
|      - Import edge weight = 1                                          |
|      - Type relationship = 0.5                                         |
|                                                                        |
|   2. Compute bottom k eigenvectors (excluding first)                   |
|                                                                        |
|   3. Embed each code entity as k-dimensional vector                    |
|                                                                        |
|   Applications:                                                        |
|   - Code similarity search (nearest neighbors)                         |
|   - Visualization (k=2 or 3)                                           |
|   - Feature input to ML models                                         |
|                                                                        |
+-----------------------------------------------------------------------+
```

**Comparison with Other Embeddings**:

| Method | Preserves | Computation | Code Use |
|--------|-----------|-------------|----------|
| Laplacian Eigenmaps | Local structure | O(|E|k) sparse | Module clustering |
| DeepWalk/node2vec | Path statistics | O(n * walks) | Similarity search |
| Graph Neural Nets | Features + structure | O(|E|Ld) per layer | Classification |

---

## Computational Considerations

### Sparse Eigensolvers

For large sparse graphs, iterative methods are essential.

**Lanczos Algorithm**:

```
+-----------------------------------------------------------------------+
|                    Lanczos Algorithm                                   |
+-----------------------------------------------------------------------+
|                                                                        |
|   Goal: Find k smallest eigenvalues/vectors of sparse symmetric L      |
|                                                                        |
|   Key insight: Only uses matrix-vector products L*v                    |
|                Never forms L explicitly                                |
|                                                                        |
|   Algorithm:                                                           |
|   1. Start with random vector v_1 (normalized)                        |
|   2. For j = 1, 2, ..., m:                                            |
|      w = L * v_j                                                      |
|      alpha_j = v_j^T * w                                              |
|      w = w - alpha_j * v_j - beta_{j-1} * v_{j-1}                     |
|      beta_j = ||w||                                                    |
|      v_{j+1} = w / beta_j                                             |
|   3. Form tridiagonal matrix T from alpha, beta                       |
|   4. Eigenvalues of T approximate eigenvalues of L                    |
|                                                                        |
|   Complexity: O(m * |E|) for m iterations                             |
|   Typically m = O(k) to O(k * log(n)) iterations suffice              |
|                                                                        |
+-----------------------------------------------------------------------+
```

**Numerical Stability**:

```
Issue: Loss of orthogonality in Lanczos vectors

Solutions:
- Full reorthogonalization: O(m^2 n) - expensive but stable
- Selective reorthogonalization: Only reorthogonalize when needed
- Thick restart: Periodically restart with best Ritz vectors

Practical advice: Use established libraries (ARPACK, SLEPc)
```

**Software Implementations**:

| Library | Language | Notes |
|---------|----------|-------|
| ARPACK | Fortran/C | Industry standard |
| scipy.sparse.linalg | Python | ARPACK wrapper |
| Spectra | C++ | Header-only |
| LOBPCG | Various | Preconditioned variant |

### Approximation for Large Graphs

**Scaling Strategies**:

| Graph Size | Strategy | Trade-off |
|------------|----------|-----------|
| < 10K nodes | Exact eigendecomposition | Best accuracy |
| 10K - 100K | Sparse Lanczos | Good accuracy, hours |
| 100K - 1M | Nystrom approximation | Approximate, minutes |
| > 1M | Randomized SVD + sampling | Approximate, scalable |

**Randomized SVD**:

```
Approximate top-k SVD in O(n * k * log(k)) time:

1. Generate random matrix Omega (n x (k + p))
2. Compute Y = L * Omega
3. Orthonormalize Y -> Q
4. Compute small matrix B = Q^T * L * Q
5. Eigendecompose B
6. Recover approximate eigenvectors
```

**Power Iteration for Dominant Eigenvector**:

```sql
-- Iterative computation of principal eigenvector (simplified)
WITH RECURSIVE power_iter AS (
    -- Initialize with random vector (approximated by node degree)
    SELECT
        node,
        1.0 / sqrt(COUNT(*) OVER ()) as x,
        0 as iteration
    FROM node_table

    UNION ALL

    -- Power iteration step
    SELECT
        n.node,
        -- Matrix-vector product with Laplacian
        (d.degree * p.x - SUM(e.weight * p2.x)) / norm.total,
        p.iteration + 1
    FROM node_table n
    JOIN power_iter p ON n.node = p.node
    JOIN degree_table d ON n.node = d.node
    LEFT JOIN edge e ON n.node = e.source
    LEFT JOIN power_iter p2 ON e.target = p2.node AND p2.iteration = p.iteration
    CROSS JOIN (
        SELECT sqrt(SUM(val*val)) as total
        FROM (
            SELECT d.degree * p.x - COALESCE(SUM(e.weight * p2.x), 0) as val
            FROM node_table n
            JOIN power_iter p ON n.node = p.node
            JOIN degree_table d ON n.node = d.node
            LEFT JOIN edge e ON n.node = e.source
            LEFT JOIN power_iter p2 ON e.target = p2.node
            GROUP BY n.node, d.degree, p.x
        )
    ) norm
    WHERE p.iteration < 50
    GROUP BY n.node, d.degree, p.x, p.iteration, norm.total
)
SELECT node, x as eigenvector_component
FROM power_iter
WHERE iteration = (SELECT MAX(iteration) FROM power_iter);
```

---

## References

### Foundational Papers

| Paper | Year | Contribution |
|-------|------|--------------|
| [Fiedler, "Algebraic connectivity of graphs"](https://www.sciencedirect.com/science/article/pii/0012365X73901088) | 1973 | Algebraic connectivity, Fiedler vector |
| [Cheeger, "A lower bound for the smallest eigenvalue"](https://projecteuclid.org/journals/pacific-journal-of-mathematics/volume-25/issue-1/A-lower-bound-for-the-smallest-eigenvalue-of-the-Laplacian/pjm/1102987136.full) | 1970 | Cheeger inequality |
| [Shi & Malik, "Normalized cuts and image segmentation"](https://ieeexplore.ieee.org/document/868688) | 2000 | NCut for clustering |
| [Ng, Jordan, Weiss, "On spectral clustering"](https://papers.nips.cc/paper/2001/hash/801272ee79cfde7fa5960571fee36b9b-Abstract.html) | 2001 | Practical spectral clustering |

### Tutorials and Surveys

| Resource | Focus |
|----------|-------|
| [Spielman, "Spectral Graph Theory" (Chapter 16)](http://www.cs.yale.edu/homes/spielman/PAPERS/SGTChapter.pdf) | Comprehensive introduction |
| [von Luxburg, "A Tutorial on Spectral Clustering"](https://arxiv.org/abs/0711.0189) | Practical guide |
| [Shuman et al., "The Emerging Field of Signal Processing on Graphs"](https://arxiv.org/abs/1211.0053) | Graph signal processing |

### Algorithms and Methods

| Topic | Reference |
|-------|-----------|
| [Lanczos Algorithm](https://en.wikipedia.org/wiki/Lanczos_algorithm) | Sparse eigensolver |
| [Nystrom Approximation](https://arxiv.org/abs/2006.14470) | Scalable spectral clustering |
| [Laplacian Eigenmaps](https://www2.imm.dtu.dk/projects/manifold/Papers/Laplacian.pdf) | Belkin & Niyogi, 2003 |
| [Graph Wavelets](https://www.sciencedirect.com/science/article/pii/S1063520310000552) | Hammond et al., 2011 |

### Cautions and Limitations

| Paper | Key Finding |
|-------|-------------|
| [von Luxburg et al., "Hitting and commute times in large graphs"](https://arxiv.org/abs/1003.1266) | Commute distance fails for large graphs |
| [Precision issues with Nystrom](https://www.ijcai.org/proceedings/2017/0347.pdf) | Nystrom can produce invalid results |

### DuckDB and Implementation

| Resource | Description |
|----------|-------------|
| [DuckDB Recursive CTEs](https://duckdb.org/docs/sql/query_syntax/with.html) | Iterative graph algorithms |
| [DuckPGQ](https://duckpgq.org/) | Graph pattern matching |
| [USING KEY optimization](https://duckdb.org/2025/05/23/using-key) | Fast convergent iterations |

---

*Spectral graph theory transforms combinatorial problems into linear algebra - the eigenvalues encode global structure while eigenvectors provide optimal embeddings for clustering and analysis.*
