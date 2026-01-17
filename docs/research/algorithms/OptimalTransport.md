# Optimal Transport Theory

Comprehensive documentation on optimal transport theory and its applications to document similarity, including the Wasserstein distance, Word Mover's Distance, and computational methods for comparing probability distributions over embeddings.

## Table of Contents

1. [Overview](#overview)
2. [Monge and Kantorovich Formulations](#monge-and-kantorovich-formulations)
3. [Wasserstein Distance](#wasserstein-distance)
4. [Computational Aspects](#computational-aspects)
5. [Word Mover's Distance (WMD)](#word-movers-distance-wmd)
6. [Sliced Wasserstein Distance](#sliced-wasserstein-distance)
7. [Optimal Transport for Embeddings](#optimal-transport-for-embeddings)
8. [Gromov-Wasserstein Distance](#gromov-wasserstein-distance)
9. [Unbalanced Optimal Transport](#unbalanced-optimal-transport)
10. [Applications to Code Search](#applications-to-code-search)
11. [References](#references)

---

## Overview

### Why Optimal Transport Matters for Document Similarity

Traditional similarity measures between documents face fundamental limitations:

| Method | Limitation |
|--------|-----------|
| Cosine similarity | Single point comparison; loses distributional structure |
| Bag-of-words | No semantic awareness; exact matching only |
| Jaccard similarity | Ignores word importance and semantics |
| TF-IDF | Still fundamentally lexical; no synonym handling |

Optimal transport provides a principled geometric framework for comparing probability distributions, treating documents as distributions over words or embeddings.

### The Earth Mover's Intuition

```
Document A: "The cat sat on the mat"
Document B: "A feline rested on the rug"

Traditional comparison:              Optimal Transport view:
========================            =======================

word overlap = 2/11 = 18%           How much "work" to transform
(only "the", "on" match)            word distribution A into B?

                                    cat -> feline     (small cost)
                                    sat -> rested     (small cost)
                                    mat -> rug        (small cost)
                                    the -> a          (small cost)

                                    Total transport cost: LOW
                                    => Documents are SIMILAR
```

Optimal transport captures semantic similarity by measuring the minimum "work" required to transform one distribution into another, where work is defined by the ground distance between elements.

### Key Advantages

```
+-------------------------------------------------------------------+
|              OPTIMAL TRANSPORT BENEFITS                            |
+-------------------------------------------------------------------+
|                                                                    |
|  1. GEOMETRY-AWARE                                                 |
|     - Respects underlying metric structure of embedding space      |
|     - "cat" and "feline" are close; "cat" and "quantum" are far   |
|                                                                    |
|  2. HANDLES VARIABLE MASS                                          |
|     - Documents of different lengths naturally compared            |
|     - No forced normalization artifacts                            |
|                                                                    |
|  3. MULTI-WORD ALIGNMENT                                           |
|     - Finds optimal matching between word sets                     |
|     - One-to-many and many-to-one mappings possible               |
|                                                                    |
|  4. INTERPRETABLE                                                  |
|     - Transport plan shows which words aligned                     |
|     - Provides explanation for similarity score                    |
|                                                                    |
+-------------------------------------------------------------------+
```

---

## Monge and Kantorovich Formulations

### Historical Background

The optimal transport problem has a rich history spanning over two centuries:

- **1781**: Gaspard Monge formulated the problem of moving earth with minimal effort
- **1942**: Leonid Kantorovich relaxed the problem, enabling linear programming solutions
- **1975**: Kantorovich received the Nobel Prize in Economics (shared)
- **1987**: Yann Brenier connected OT to fluid mechanics and geometry
- **2010**: Cedric Villani received the Fields Medal for work on OT and related topics

### The Monge Problem (Original Formulation)

Monge posed the question: given two distributions of mass, find a transport map T that moves mass from one to the other with minimal total cost.

#### Formal Definition

```
Given:
  - Source measure mu on space X
  - Target measure nu on space Y
  - Cost function c: X x Y -> R (cost to move unit mass from x to y)

Find:
  Transport map T: X -> Y minimizing:

    inf   INTEGRAL c(x, T(x)) d mu(x)
    T       X

  subject to: T pushes mu forward to nu
              (mass conservation: nu(B) = mu(T^{-1}(B)) for all B)
```

#### Monge Problem Limitations

```
PROBLEM: Monge's formulation requires a deterministic map T.

Example where no Monge map exists:

Source: Single point mass at x=0
        |
        * (mass = 2)
        |
        +------------------

Target: Two point masses at x=1, x=2
               |         |
               * (1)     * (1)
               |         |
        -------+---------+------

No function T can split the mass at x=0 into two pieces!
Monge problem is INFEASIBLE for this case.
```

### The Kantorovich Relaxation

Kantorovich's key insight: instead of requiring a deterministic map, allow probabilistic transport plans that can split mass.

#### Formal Definition

```
Given:
  - Source measure mu in P(X)
  - Target measure nu in P(Y)
  - Cost function c: X x Y -> R

Find:
  Coupling (joint distribution) gamma in PI(mu, nu) minimizing:

    inf         INTEGRAL INTEGRAL c(x, y) d gamma(x, y)
    gamma in PI(mu,nu)   X        Y

  where PI(mu, nu) = {gamma in P(X x Y) :
                       marginal_X(gamma) = mu,
                       marginal_Y(gamma) = nu}
```

#### Coupling Interpretation

```
+-------------------------------------------+
|         COUPLING / TRANSPORT PLAN          |
+-------------------------------------------+
|                                           |
|  gamma(x, y) = amount of mass moved       |
|                from x to y                |
|                                           |
|  Marginal constraints:                    |
|    SUM gamma(x, y) = mu(x)  (all mass at x is moved out)
|     y                                     |
|    SUM gamma(x, y) = nu(y)  (all mass at y comes from somewhere)
|     x                                     |
|                                           |
|  gamma can SPLIT mass (unlike Monge map)  |
|                                           |
+-------------------------------------------+
```

### Linear Programming Formulation

For discrete measures, Kantorovich's problem becomes a finite-dimensional linear program.

#### Discrete Setup

```
Source: mu = SUM_{i=1}^n  a_i * delta_{x_i}   (n point masses)
Target: nu = SUM_{j=1}^m  b_j * delta_{y_j}   (m point masses)

Cost matrix: C_ij = c(x_i, y_j)

Transport plan: P_ij = mass moved from x_i to y_j
```

#### LP Formulation

```
minimize    SUM_i SUM_j  C_ij * P_ij
   P

subject to: SUM_j P_ij = a_i    for all i  (row sums = source masses)
            SUM_i P_ij = b_j    for all j  (col sums = target masses)
            P_ij >= 0           for all i,j  (non-negative transport)

Variables: n x m matrix P
Constraints: n + m equality constraints (one redundant due to mass balance)
```

#### Example: Discrete Transport

```
Source distribution (documents/words):
x_1: "authentication"  mass = 0.4
x_2: "token"           mass = 0.3
x_3: "validate"        mass = 0.3

Target distribution:
y_1: "auth"            mass = 0.35
y_2: "JWT"             mass = 0.35
y_3: "verify"          mass = 0.30

Cost matrix (based on embedding distance):
        y_1(auth) y_2(JWT) y_3(verify)
x_1(auth)  0.1      0.3      0.5
x_2(token) 0.4      0.2      0.6
x_3(valid) 0.5      0.5      0.15

Optimal transport plan P*:
        y_1     y_2     y_3
x_1     0.35    0.05    0.0     (authentication -> mostly auth, some JWT)
x_2     0.0     0.30    0.0     (token -> all to JWT)
x_3     0.0     0.0     0.30    (validate -> all to verify)

Transport cost = 0.35*0.1 + 0.05*0.3 + 0.30*0.2 + 0.30*0.15
               = 0.035 + 0.015 + 0.06 + 0.045 = 0.155
```

### Kantorovich Duality

The Kantorovich problem has a powerful dual formulation:

```
Primal (Transport):
    inf         INTEGRAL c(x,y) d gamma(x,y)
    gamma in PI(mu,nu)

Dual (Potential Functions):
    sup         INTEGRAL phi d mu + INTEGRAL psi d nu
    phi, psi
    subject to: phi(x) + psi(y) <= c(x,y)  for all x, y

Strong Duality: Primal optimal = Dual optimal (under mild conditions)
```

#### Dual Interpretation

```
phi(x) = "price paid" for picking up mass at x
psi(y) = "price received" for delivering mass to y

Constraint phi(x) + psi(y) <= c(x,y):
"Total payment <= actual transport cost"

Optimal potentials (phi*, psi*) satisfy:
- phi*(x) + psi*(y) = c(x,y) whenever gamma*(x,y) > 0
  (active transport routes have zero profit margin)
```

---

## Wasserstein Distance

### Definition: p-Wasserstein Distance

The optimal transport cost with p-th power cost function defines the p-Wasserstein distance.

```
W_p(mu, nu) = ( inf         INTEGRAL d(x,y)^p d gamma(x,y) )^{1/p}
               gamma in PI(mu,nu)

where:
  d(x, y) = ground metric on the space (e.g., Euclidean distance)
  p >= 1  = order of the Wasserstein distance
```

### Common Cases

| Order | Name | Formula | Properties |
|-------|------|---------|------------|
| p = 1 | W_1, Kantorovich-Rubinstein, Earth Mover's Distance | W_1 = inf INTEGRAL d(x,y) d gamma | Linear in distance; dual form involves Lipschitz functions |
| p = 2 | W_2, Wasserstein-2 | W_2 = (inf INTEGRAL d(x,y)^2 d gamma)^{1/2} | Corresponds to optimal transport map (Brenier); connects to geometry |
| p = infinity | W_inf | sup_{(x,y) in supp(gamma)} d(x,y) | Maximum distance traveled |

### 1-Wasserstein: The Earth Mover's Distance (EMD)

The 1-Wasserstein distance has the most intuitive interpretation:

```
W_1(mu, nu) = "minimum total work to transform mu into nu"
            = SUM (mass moved) * (distance moved)

Dual formulation (Kantorovich-Rubinstein):

W_1(mu, nu) = sup   | INTEGRAL f d mu - INTEGRAL f d nu |
              ||f||_L <= 1

where ||f||_L = sup |f(x) - f(y)| / d(x,y)  (Lipschitz constant)
               x != y

This says: W_1 is the maximum difference in expectations over
           all 1-Lipschitz test functions.
```

### 1D Wasserstein: Closed-Form Solution

In one dimension, the Wasserstein distance has a beautiful closed form:

```
For 1D distributions with CDFs F and G:

W_p(F, G) = ( INTEGRAL_0^1 |F^{-1}(t) - G^{-1}(t)|^p dt )^{1/p}

For p = 1:
W_1(F, G) = INTEGRAL_{-inf}^{inf} |F(x) - G(x)| dx

Interpretation: Area between the two CDFs
```

```
        1 |        ____----F(x)
          |    ___/    /
          |   /  AREA /___----G(x)
          |  /      _/
          | /   ___/
        0 |/_____-------------------
          |-----------------------x

W_1 = shaded area between F and G
```

### Wasserstein Distance as a Metric

The Wasserstein distance satisfies all metric axioms:

```
1. Non-negativity:  W_p(mu, nu) >= 0
                    W_p(mu, nu) = 0  iff  mu = nu

2. Symmetry:        W_p(mu, nu) = W_p(nu, mu)

3. Triangle inequality:
   W_p(mu, rho) <= W_p(mu, nu) + W_p(nu, rho)
```

### Properties Relevant to Document Similarity

| Property | Implication for Documents |
|----------|---------------------------|
| Metrizes weak convergence | Small W distance => similar expectations for smooth functions |
| Continuous in measures | Adding a word slightly changes distance (no discontinuities) |
| Respects geometry | Uses ground metric structure of embedding space |
| Not dependent on support | Can compare documents with different vocabularies |

### Comparison with Other Distances

```
+------------------------------------------------------------------+
|     DISTANCE MEASURE COMPARISON FOR DISTRIBUTIONS                 |
+------------------------------------------------------------------+
|                                                                   |
| KL Divergence: D_KL(P || Q) = SUM P(x) log(P(x)/Q(x))            |
|   - Not symmetric, not a metric                                   |
|   - Undefined if Q(x) = 0 where P(x) > 0                         |
|   - Ignores geometry of underlying space                          |
|                                                                   |
| Total Variation: TV(P, Q) = 0.5 * SUM |P(x) - Q(x)|              |
|   - Metric, but ignores geometry                                  |
|   - "cat" vs "dog" same as "cat" vs "quantum"                    |
|                                                                   |
| Wasserstein: W_1(P, Q) = min transport cost                       |
|   - True metric with triangle inequality                          |
|   - Incorporates ground metric (embedding distances)              |
|   - "cat" vs "dog" < "cat" vs "quantum"                          |
|                                                                   |
+------------------------------------------------------------------+
```

---

## Computational Aspects

### Complexity of Exact Optimal Transport

The linear programming formulation can be solved exactly, but the cost is significant:

```
Problem size: n source points, m target points

Variables:    n * m  (transport matrix entries)
Constraints:  n + m  (marginal constraints)

Complexity of LP solvers:
  - Interior point: O(n^3) to O(n^3.5) typically
  - Simplex: O(n^3) average, O(n * 2^n) worst case
```

### Network Simplex Algorithm

The transportation problem has special structure exploited by the network simplex algorithm:

```
+--------------------------------------------------+
|          NETWORK SIMPLEX FOR OT                  |
+--------------------------------------------------+
|                                                  |
| Structure: Transportation problem is a network   |
|            flow on a bipartite graph             |
|                                                  |
|    Sources (x_i)        Targets (y_j)           |
|        *--------------->*                        |
|        | \             /|                        |
|        |  \    cost   / |                        |
|        |   \  c_ij   /  |                        |
|        |    \       /   |                        |
|        *-----+-----+----*                        |
|        |      \   /     |                        |
|        |       \ /      |                        |
|        *--------*-------*                        |
|                                                  |
| Key insight: Basic feasible solutions correspond |
|              to spanning trees with n+m-1 edges  |
|                                                  |
| Per iteration: O(n + m)                          |
| Total iterations: O(n * m) typical               |
| Overall: O(n^2 * m) or O(n * m^2)               |
|                                                  |
+--------------------------------------------------+
```

### Sinkhorn Algorithm: Entropic Regularization

Cuturi (2013) introduced entropic regularization to enable fast approximate OT:

#### Regularized Problem

```
Regularized OT (entropic):

    min         <C, P> - epsilon * H(P)
    P in PI(mu, nu)

where:
  <C, P> = SUM_{i,j} C_ij * P_ij  (transport cost)
  H(P) = -SUM_{i,j} P_ij log(P_ij)  (entropy of transport plan)
  epsilon > 0  (regularization strength)

As epsilon -> 0: Solution approaches exact OT
As epsilon -> inf: Solution approaches independent coupling mu x nu
```

#### Sinkhorn Iteration

The regularized problem has a unique solution with special structure:

```
Optimal transport plan: P*_ij = u_i * K_ij * v_j

where:
  K_ij = exp(-C_ij / epsilon)  (Gibbs kernel)
  u, v = scaling vectors satisfying:
    u_i * SUM_j K_ij * v_j = a_i  (row sums = source masses)
    v_j * SUM_i u_i * K_ij = b_j  (col sums = target masses)
```

```
Algorithm: Sinkhorn(C, a, b, epsilon, max_iter)
===============================================
Input:  C = cost matrix, a,b = marginals, epsilon = regularization
Output: P = approximate transport plan

K <- exp(-C / epsilon)   # Gibbs kernel, element-wise
u <- ones(n)             # Initialize scaling
v <- ones(m)

for t = 1 to max_iter:
    u <- a ./ (K @ v)    # Row scaling
    v <- b ./ (K.T @ u)  # Column scaling

P <- diag(u) @ K @ diag(v)  # Final transport plan
return P
```

#### Sinkhorn Complexity and Convergence

```
Per iteration: O(n * m)  (matrix-vector products)
               O(n^2) for equal-sized problems

Convergence: Linear rate, depends on epsilon
             Larger epsilon => faster convergence, more blur
             Smaller epsilon => slower convergence, closer to exact

Typical choice: epsilon ~ 0.01 * median(C)
                iterations ~ 100-500 for good approximation

GPU-friendly: Matrix operations parallelize well
              Order of magnitude faster than network simplex on GPU
```

#### Numerical Stability: Log-Domain Sinkhorn

For small epsilon, the Gibbs kernel K has entries near 0 and 1, causing underflow:

```
Algorithm: Log_Sinkhorn(C, a, b, epsilon, max_iter)
===================================================
# Work in log domain to avoid underflow

log_K <- -C / epsilon
f <- zeros(n)  # log(u)
g <- zeros(m)  # log(v)

for t = 1 to max_iter:
    f <- log(a) - logsumexp(log_K + g, axis=1)
    g <- log(b) - logsumexp(log_K.T + f, axis=1)

# Transport plan in log domain
log_P <- f[:, None] + log_K + g[None, :]
P <- exp(log_P)
return P
```

### Complexity Comparison

| Algorithm | Time Complexity | Space | GPU-Friendly | Exact |
|-----------|-----------------|-------|--------------|-------|
| Network Simplex | O(n^2 * m) | O(n + m) | No | Yes |
| Interior Point LP | O(n^3) | O(n^2) | Partially | Yes |
| Sinkhorn | O(L * n * m) | O(n * m) | Yes | No (approx) |
| Sinkhorn (log-domain) | O(L * n * m) | O(n * m) | Yes | No (approx) |

Where L = number of Sinkhorn iterations (typically 100-500).

### Approximation Quality

```
Sinkhorn approximation error:

W_epsilon(mu, nu) - W(mu, nu) <= epsilon * H(gamma*)

where gamma* = optimal unregularized plan
      H(gamma*) = entropy of optimal plan

Practical guidance:
- epsilon = 0.1 * mean(C): ~10% relative error
- epsilon = 0.01 * mean(C): ~1% relative error
- epsilon = 0.001 * mean(C): numerically unstable without log-domain
```

---

## Word Mover's Distance (WMD)

### Motivation: Semantic Document Similarity

Traditional document similarity fails when documents express the same concepts using different words:

```
Document A: "Obama speaks to the media in Illinois"
Document B: "The President greets the press in Chicago"

Bag-of-words overlap: 3 / 12 = 25% (only "the", "to", "in")

But semantically:
  Obama ~ President (same person)
  speaks ~ greets (similar action)
  media ~ press (same concept)
  Illinois ~ Chicago (geographic relation)

These documents are HIGHLY similar despite low word overlap.
```

### WMD Definition (Kusner et al., 2015)

The Word Mover's Distance casts document similarity as an optimal transport problem:

```
Given:
  - Word embedding function: word -> R^d (e.g., Word2Vec, GloVe)
  - Document d represented as bag of words: d = (w_1, w_2, ..., w_n)
  - Normalized word frequency: f_i = count(w_i) / |d|
  - Ground metric: c(w_i, w_j) = ||embed(w_i) - embed(w_j)||_2

WMD(d, d') = min     SUM_i SUM_j T_ij * c(w_i, w'_j)
             T >= 0
             subject to: SUM_j T_ij = f_i     (words in d must be transported out)
                         SUM_i T_ij = f'_j    (words in d' must receive transport)
```

### WMD Example

```
Document d:  "The cat sat on the mat"
Document d': "The feline rested on the rug"

After removing stopwords and normalizing:
d  = {cat: 0.5, sat: 0.25, mat: 0.25}
d' = {feline: 0.5, rested: 0.25, rug: 0.25}

Embedding distances (hypothetical):
        feline  rested   rug
cat      0.2     0.8     0.9
sat      0.8     0.3     0.9
mat      0.9     0.8     0.3

Optimal transport plan:
        feline  rested   rug
cat      0.5     0.0     0.0    (all cat -> feline)
sat      0.0     0.25    0.0    (all sat -> rested)
mat      0.0     0.0     0.25   (all mat -> rug)

WMD = 0.5 * 0.2 + 0.25 * 0.3 + 0.25 * 0.3 = 0.1 + 0.075 + 0.075 = 0.25

Low WMD indicates high semantic similarity despite no word overlap!
```

### WMD Properties

| Property | Description |
|----------|-------------|
| Hyperparameter-free | No tuning required; uses embedding distances directly |
| Metric | Satisfies triangle inequality (inherits from Wasserstein) |
| Interpretable | Transport plan shows word alignments |
| Embedding-dependent | Quality depends on embedding quality |

### Computational Complexity of WMD

```
Exact WMD via EMD solver:
  - n unique words in document d
  - m unique words in document d'
  - Complexity: O(n^3 log n) using network simplex

For comparing query to N documents:
  - Total: O(N * n^3 log n)
  - Impractical for large-scale retrieval
```

### Relaxed Word Mover's Distance (RWMD)

Kusner et al. proposed a fast lower bound by relaxing one set of constraints:

```
RWMD_1: Remove target constraints

    RWMD_1(d, d') = SUM_i f_i * min_j c(w_i, w'_j)

    "Each word in d goes to its nearest neighbor in d'"

RWMD_2: Remove source constraints

    RWMD_2(d, d') = SUM_j f'_j * min_i c(w_i, w'_j)

    "Each word in d' receives from its nearest neighbor in d"

Combined lower bound:
    RWMD(d, d') = max(RWMD_1, RWMD_2) <= WMD(d, d')
```

#### RWMD Complexity

```
RWMD computation:
  - For each word in d, find nearest word in d': O(n * m)
  - For each word in d', find nearest word in d: O(m * n)
  - Total: O(n * m)

Much faster than O(n^3) for exact WMD!

With pre-built nearest neighbor index on embeddings:
  - Approximate NN: O(n * log(vocab)) or O(n) with hashing
  - Even faster for repeated queries
```

#### RWMD Quality

```
Empirical findings (Kusner et al., 2015):

k-NN classification accuracy with WMD vs RWMD:
Dataset      WMD Error   RWMD Error  Difference
---------------------------------------------
20News       27.8%       28.6%       +0.8%
BBC          2.8%        3.2%        +0.4%
Classic      3.8%        4.1%        +0.3%

RWMD provides 72-100% overlap with WMD's k-NN results
while being orders of magnitude faster.
```

### Linear-Complexity Relaxed WMD (LC-RWMD)

Atasu et al. (2017) further accelerated RWMD:

```
Key insight: Precompute and cache word-to-document distances

For document collection {d_1, ..., d_N}:

1. Build word-level index: For each word w in vocabulary,
   store its minimum distance to each document

2. At query time for document d:
   - For each unique word w in d, look up cached distances
   - Aggregate with word frequencies

Complexity: O(|d| * |unique words in d|) per query
            (independent of collection size N!)
```

### Prefetch and Prune Strategy

For k-NN search with WMD, use RWMD to prune:

```
Algorithm: WMD_kNN(query, documents, k)
========================================
# Phase 1: Compute RWMD lower bounds
rwmd_scores <- []
for d in documents:
    rwmd_scores.append(RWMD(query, d))

# Phase 2: Prune and compute exact WMD
candidates <- top-k' by RWMD (k' >> k, e.g., 5k)
wmd_scores <- []
kth_best <- infinity

for d in candidates (sorted by RWMD):
    if RWMD(query, d) > kth_best:
        break  # Prune: RWMD is lower bound

    exact <- WMD(query, d)
    wmd_scores.append(exact)
    kth_best <- k-th smallest in wmd_scores

return top-k by exact WMD
```

### WMD Limitations

| Limitation | Description | Mitigation |
|------------|-------------|------------|
| Ignores word order | "dog bites man" = "man bites dog" | Use sequence-aware models |
| Bag-of-words assumption | No syntax or structure | Combine with structural features |
| Cubic complexity | Exact WMD is slow | Use RWMD approximation |
| Stopword sensitivity | Common words dilute signal | Remove stopwords or use TF-IDF weighting |

---

## Sliced Wasserstein Distance

### Motivation: Curse of Dimensionality

Standard Wasserstein distance in high dimensions (d >> 100) is:
1. Computationally expensive: O(n^3) for n points
2. Statistically inefficient: Sample complexity grows with d

### The Slicing Idea

```
Key insight: 1D Wasserstein has closed form!

W_1(F, G) = INTEGRAL |F^{-1}(t) - G^{-1}(t)| dt

For sorted samples x_1 <= ... <= x_n and y_1 <= ... <= y_n:
W_1(X, Y) = (1/n) * SUM_i |x_i - y_i|

Complexity: O(n log n) for sorting, O(n) for sum
```

### Sliced Wasserstein Definition

Project high-dimensional distributions onto random 1D lines, compute 1D Wasserstein, and average:

```
SW_p(mu, nu) = ( INTEGRAL_{theta in S^{d-1}} W_p^p(theta . mu, theta . nu) d theta )^{1/p}

where:
  S^{d-1} = unit sphere in R^d
  theta . mu = pushforward of mu onto line through origin with direction theta
  theta . nu = pushforward of nu onto line through origin with direction theta

Monte Carlo approximation:
  SW_p(mu, nu) ~ ( (1/L) SUM_{l=1}^L W_p^p(theta_l . mu, theta_l . nu) )^{1/p}

  where theta_1, ..., theta_L sampled uniformly from S^{d-1}
```

### Sliced Wasserstein Algorithm

```
Algorithm: Sliced_Wasserstein(X, Y, L, p)
=========================================
Input: X = n samples from mu, Y = m samples from nu
       L = number of projections, p = Wasserstein order
Output: Approximate SW_p distance

total <- 0

for l = 1 to L:
    # Sample random direction
    theta <- random_unit_vector(d)

    # Project samples onto line
    proj_X <- X @ theta  # n-dimensional
    proj_Y <- Y @ theta  # m-dimensional

    # Sort projections
    sort(proj_X)
    sort(proj_Y)

    # Compute 1D Wasserstein (need to handle different sizes)
    # Interpolate if n != m
    w_1d <- compute_1d_wasserstein(proj_X, proj_Y, p)
    total <- total + w_1d^p

return (total / L)^{1/p}
```

### Complexity Analysis

```
Per projection:
  - Random vector generation: O(d)
  - Projection (matrix-vector): O(n * d) for n samples
  - Sorting: O(n log n)
  - 1D Wasserstein: O(n)

Total: O(L * (n * d + n log n))
     = O(L * n * (d + log n))

Compare to exact d-dimensional Wasserstein: O(n^3)

For n = 10,000, d = 384, L = 100:
  SW: ~100 * 10000 * (384 + 14) ~ 400M operations
  Exact: ~10000^3 = 10^12 operations

Speedup: ~2500x
```

### Sliced Wasserstein Properties

| Property | Status | Notes |
|----------|--------|-------|
| Metric | Yes | Inherits from 1D Wasserstein |
| Statistical efficiency | O(1/sqrt(n)) | Independent of dimension! |
| Computational complexity | O(L * n * d) | Linear in n and d |
| Approximation quality | Depends on L | L ~ 50-500 typically sufficient |

### Sliced Wasserstein Variants

```
+------------------------------------------------------------------+
|              SLICED WASSERSTEIN VARIANTS                          |
+------------------------------------------------------------------+
|                                                                   |
| MAX-SLICED WASSERSTEIN (Max-SW):                                 |
|   Find the projection direction that maximizes 1D Wasserstein     |
|   max_{theta} W_p(theta . mu, theta . nu)                        |
|   - More discriminative than average                              |
|   - Requires optimization over S^{d-1}                           |
|                                                                   |
| GENERALIZED SLICED WASSERSTEIN (GSW):                            |
|   Project onto curves/surfaces instead of lines                   |
|   - Circular projections, polynomial curves                       |
|   - Can capture non-linear structure                              |
|                                                                   |
| HIERARCHICAL SLICED WASSERSTEIN (HSW):                           |
|   Multi-resolution projection hierarchy                           |
|   - Faster convergence                                            |
|   - Better for structured data                                    |
|                                                                   |
| AUGMENTED SLICED WASSERSTEIN (ASW):                              |
|   Learn projection function with neural network                   |
|   - Adaptive to data distribution                                 |
|   - Requires training                                             |
|                                                                   |
+------------------------------------------------------------------+
```

### When to Use Sliced Wasserstein

| Scenario | Recommendation |
|----------|----------------|
| High-dimensional embeddings (d > 100) | Sliced Wasserstein |
| Small datasets (n < 1000) | Exact Wasserstein may be feasible |
| Gradient-based optimization (GANs, etc.) | Sliced Wasserstein (differentiable) |
| Interpretability needed | Exact Wasserstein (provides transport plan) |
| Real-time applications | Sliced Wasserstein with small L |

---

## Optimal Transport for Embeddings

### Comparing Embedding Distributions

When documents are represented as sets of embeddings (e.g., one per token), OT provides a principled comparison:

```
Document A -> {e_1^A, e_2^A, ..., e_n^A}  (token embeddings)
Document B -> {e_1^B, e_2^B, ..., e_m^B}  (token embeddings)

Empirical distributions:
mu_A = (1/n) * SUM_i delta_{e_i^A}
mu_B = (1/m) * SUM_j delta_{e_j^B}

Document similarity = W_p(mu_A, mu_B)^{-1} or exp(-W_p(mu_A, mu_B))
```

### Advantages Over Single-Vector Comparison

```
Traditional (single vector per document):
==========================================
doc_A -> mean_pool(embeddings_A) -> v_A in R^d
doc_B -> mean_pool(embeddings_B) -> v_B in R^d

similarity = cosine(v_A, v_B)

Problems:
- Information loss: All tokens compressed into one vector
- Cancellation: Opposite-meaning tokens can cancel out
- Ignores distribution shape


Optimal Transport (distribution over embeddings):
=================================================
doc_A -> {e_1^A, ..., e_n^A}  (preserve all embeddings)
doc_B -> {e_1^B, ..., e_m^B}

similarity = 1 / (1 + W_p(mu_A, mu_B))

Benefits:
- Preserves token-level information
- Finds optimal alignment between tokens
- Captures distribution shape and spread
```

### Wasserstein Barycenters

The Wasserstein barycenter is the "average" of multiple distributions in OT geometry:

```
Given distributions mu_1, ..., mu_K with weights lambda_1, ..., lambda_K:

Wasserstein barycenter = argmin     SUM_k lambda_k * W_2^2(nu, mu_k)
                          nu in P(X)

Properties:
- Interpolates between distributions geometrically
- Preserves multi-modal structure (unlike Euclidean averaging)
- Unique for W_2 with strictly convex cost
```

#### Barycenter Visualization

```
Euclidean average:              Wasserstein barycenter:
==================              =====================

mu_1   mu_2   avg               mu_1   mu_2   bary
 |      |      |                 |      |      |
 *      *      *                 *      *     * *
 |             |                 |            | |
 *             *                 *            * *

Two point masses               Two point masses preserved!
become ONE smeared mass        (barycenters have support at
                               interpolated positions)
```

#### Applications of Wasserstein Barycenters

| Application | Description |
|-------------|-------------|
| Document summarization | Barycenter of sentence embeddings |
| Topic modeling | Barycenter as topic centroid |
| Domain adaptation | Align source and target domains |
| Data augmentation | Generate intermediate examples |
| Clustering | Barycenter as cluster representative |

### Computational Methods for Barycenters

```
Algorithm: Sinkhorn_Barycenter(mu_list, weights, epsilon, max_iter)
==================================================================
# Free-support barycenter with fixed support points

Initialize support points X (e.g., from random mu samples)
Initialize barycenter weights b (uniform)

for t = 1 to max_iter:
    # Compute transport to each mu_k
    for k = 1 to K:
        P_k <- Sinkhorn(C(X, supp(mu_k)), b, mu_k.weights, epsilon)

    # Update barycenter support (gradient descent)
    for i = 1 to |X|:
        grad_i <- SUM_k weights[k] * SUM_j P_k[i,j] * (X[i] - supp(mu_k)[j])
        X[i] <- X[i] - learning_rate * grad_i

    # Update barycenter weights
    b <- (1/K) * SUM_k P_k.sum(axis=1)

return (X, b)
```

---

## Gromov-Wasserstein Distance

### Motivation: Comparing Incomparable Spaces

Standard Wasserstein requires a ground metric between the two spaces. But what if:
- Documents are in different languages (different embedding spaces)?
- Comparing code structure to documentation?
- Different modalities (text vs. images)?

### Gromov-Wasserstein Formulation

Instead of comparing points across spaces, compare *distances within* each space:

```
Given:
  - (X, d_X, mu): metric measure space 1
  - (Y, d_Y, nu): metric measure space 2

Gromov-Wasserstein distance:

GW(mu, nu) = ( inf         INTEGRAL INTEGRAL |d_X(x,x') - d_Y(y,y')|^p
               gamma in PI(mu,nu)    d gamma(x,y) d gamma(x',y') )^{1/p}

Interpretation:
  Find coupling gamma that minimizes distortion of pairwise distances.
  If x and x' are close in X, their matched points y and y' should be
  close in Y.
```

### GW Intuition

```
Space X (English embeddings):     Space Y (French embeddings):

  king *---0.3---* man              roi *---0.3---* homme
       |                                 |
      0.4                               0.4
       |                                 |
  queen *---0.3---* woman           reine *---0.3---* femme

Distance structure is similar despite different embedding spaces!
GW finds optimal alignment: king<->roi, queen<->reine, etc.
```

### GW Properties

| Property | Description |
|----------|-------------|
| Metric on mm-spaces | Up to isometry |
| NP-hard to compute | Quadratic assignment problem |
| Structure-preserving | Aligns based on internal geometry |
| Does not need cross-space metric | Only requires within-space metrics |

### Entropic Gromov-Wasserstein

Similar to Sinkhorn for Wasserstein, entropic regularization enables tractable GW:

```
Regularized GW:

min         SUM_{i,j,k,l} |d_X(x_i, x_k) - d_Y(y_j, y_l)|^p * P_ij * P_kl
 P in PI(a,b)
           - epsilon * H(P)

Algorithm: Alternate between:
1. Given P, compute cost tensor L_ijkl = |d_X(i,k) - d_Y(j,l)|^p
2. Sinkhorn iterations with cost C_ij = SUM_kl L_ijkl * P_kl

Complexity per iteration: O(n^2 * m^2) for computing cost tensor
                         O(n * m) for Sinkhorn
```

### Cross-Lingual Applications

```
+------------------------------------------------------------------+
|        GW FOR CROSS-LINGUAL WORD EMBEDDING ALIGNMENT              |
+------------------------------------------------------------------+
|                                                                   |
| Problem: Align English and French word embeddings without         |
|          parallel data (no word-level translations needed!)       |
|                                                                   |
| Approach (Alvarez-Melis & Jaakkola, 2018):                       |
| 1. Compute within-language similarity matrices                    |
|    D_en[i,k] = sim(embed_en(word_i), embed_en(word_k))          |
|    D_fr[j,l] = sim(embed_fr(mot_j), embed_fr(mot_l))            |
|                                                                   |
| 2. Solve GW to find word alignment                                |
|    P* = argmin GW(D_en, D_fr)                                    |
|                                                                   |
| 3. Use alignment for:                                             |
|    - Cross-lingual retrieval                                      |
|    - Unsupervised machine translation                             |
|    - Transfer learning across languages                           |
|                                                                   |
| Key insight: Semantic structure is similar across languages       |
|              (king-queen-man-woman relations preserved)           |
|                                                                   |
+------------------------------------------------------------------+
```

### GW for Multi-Modal Comparison

```
Comparing code structure to documentation:
==========================================

Code AST nodes: {class, method, variable, ...}
Doc structure:  {section, paragraph, list, ...}

Within-code distances:
  D_code[i,j] = structural_distance(node_i, node_j)

Within-doc distances:
  D_doc[i,j] = structural_distance(element_i, element_j)

GW alignment finds:
  class <-> section (major structural element)
  method <-> paragraph (detailed content)
  variable <-> list item (specific instance)

Use for:
- Code-documentation consistency checking
- Documentation generation
- Cross-modal search
```

---

## Unbalanced Optimal Transport

### Motivation: When Masses Differ

Standard OT requires:
- SUM_j P_ij = a_i (all source mass transported out)
- SUM_i P_ij = b_j (all target mass received)

But documents have different lengths, and some content may be unique to one document.

### The Unbalanced OT Formulation

Relax marginal constraints by adding divergence penalties:

```
Unbalanced OT:

min     <C, P> + rho_1 * D(P @ 1, a) + rho_2 * D(P.T @ 1, b)
 P >= 0

where:
  D = divergence (KL, TV, or other)
  rho_1, rho_2 = penalization strengths for marginal violations

  D = KL divergence case (Chizat et al., 2018):
  D_KL(u || v) = SUM_i u_i log(u_i/v_i) - u_i + v_i
```

### Interpretation

```
+------------------------------------------------------------------+
|           UNBALANCED OT INTERPRETATION                            |
+------------------------------------------------------------------+
|                                                                   |
| Allow mass creation and destruction at a cost:                    |
|                                                                   |
| Source:  *--*--*--*  (4 words, mass = 1.0)                       |
|           |  |  |                                                 |
|           v  v  v    (transport some mass)                        |
|           |  |  |                                                 |
| Target:  *--*  (2 words, mass = 0.6)                             |
|                                                                   |
| What happens to extra 0.4 mass?                                   |
|                                                                   |
| Standard OT: INFEASIBLE (mass mismatch)                          |
|                                                                   |
| Unbalanced OT: Destroy 0.4 mass at source, pay penalty           |
|                                                                   |
| Applications:                                                     |
| - Documents of different lengths                                  |
| - Handling outlier words not in other document                   |
| - Partial matching (find common content)                         |
|                                                                   |
+------------------------------------------------------------------+
```

### Unbalanced Sinkhorn

```
Algorithm: Unbalanced_Sinkhorn(C, a, b, epsilon, rho, max_iter)
===============================================================
# With KL divergence penalties

K <- exp(-C / epsilon)
u <- ones(n)
v <- ones(m)

# Modified scaling for unbalanced case
tau_1 <- rho / (rho + epsilon)
tau_2 <- rho / (rho + epsilon)

for t = 1 to max_iter:
    u <- (a ./ (K @ v))^tau_1  # Soft row scaling
    v <- (b ./ (K.T @ u))^tau_2  # Soft column scaling

P <- diag(u) @ K @ diag(v)
return P

Key difference from balanced: Exponent tau < 1 allows marginal mismatch
```

### Application to Document Comparison

```
Comparing documents of different lengths:
=========================================

Short query: "JWT authentication"  (2 significant words)
Long document: "This module handles JWT token generation,
               validation, and authentication flows using
               OAuth2 and OpenID Connect protocols."  (15+ words)

Balanced WMD: Must transport ALL query mass AND receive ALL doc mass
              Query words spread thin; doc specifics overwhelm

Unbalanced WMD:
- Query "JWT" matches doc "JWT token" strongly
- Query "authentication" matches doc "authentication" strongly
- Extra doc words (OAuth2, OpenID, etc.) incur smaller penalty
- Result: Focused similarity based on query terms

Effect: Unbalanced OT acts like asymmetric similarity,
        measuring "how well query is covered by document"
```

### Partial Optimal Transport

A special case: transport only a fraction of total mass:

```
Partial OT:

min     <C, P>
 P >= 0

subject to: P @ 1 <= a
            P.T @ 1 <= b
            1.T @ P @ 1 = m  (total mass to transport)

where m < min(sum(a), sum(b))

Use case: Find the most similar subsets of two documents
```

---

## Applications to Code Search

### Why Optimal Transport for Code?

```
Code search challenges:
=======================

1. Vocabulary mismatch:
   Query: "remove duplicates from list"
   Code:  def deduplicate(sequence): ...

   OT aligns: remove <-> deduplicate, duplicates <-> (implicit)

2. Structural variation:
   Same algorithm, different implementations
   OT finds correspondence between structural elements

3. Multi-part queries:
   "Parse JSON and handle errors"
   Need to match BOTH concepts in code
   OT ensures coverage of all query aspects
```

### WMD for Code Search

```
Adapting WMD to code:
====================

1. Tokenization:
   - Split identifiers: getUserById -> [get, User, By, Id]
   - Keep keywords: class, function, return
   - Remove language-specific noise

2. Embeddings:
   - Use code-specific embeddings (CodeBERT, GraphCodeBERT)
   - Or fine-tuned general embeddings (E5 on code)

3. Weighting:
   - Function names: high weight
   - Parameter names: medium weight
   - Comments: medium weight
   - Body tokens: lower weight

4. Ground metric:
   c(w_i, w_j) = 1 - cosine(embed(w_i), embed(w_j))
```

### Code Structure Comparison with GW

```
Comparing code structure across languages:
=========================================

Python function:
  def calculate_total(items):
      total = 0
      for item in items:
          total += item.price
      return total

JavaScript equivalent:
  function calculateTotal(items) {
      let total = 0;
      for (const item of items) {
          total += item.price;
      }
      return total;
  }

AST-based GW alignment:
  FunctionDef <-> FunctionDeclaration
  arguments <-> params
  For <-> ForOfStatement
  Return <-> ReturnStatement

GW captures structural equivalence despite syntax differences.
```

### Hierarchical OT for Multi-Scale Matching

```
+------------------------------------------------------------------+
|           HIERARCHICAL OT FOR CODE SEARCH                         |
+------------------------------------------------------------------+
|                                                                   |
| Level 1: File-level matching                                      |
|   Query "authentication" -> Which files are relevant?             |
|   OT between query concepts and file summaries                    |
|                                                                   |
| Level 2: Function-level matching                                  |
|   Within relevant files, which functions?                         |
|   OT between query aspects and function signatures                |
|                                                                   |
| Level 3: Code-level matching                                      |
|   Within relevant functions, which lines?                         |
|   WMD between query tokens and code tokens                        |
|                                                                   |
| Benefits:                                                         |
| - Computational efficiency (prune early at high levels)           |
| - Multi-resolution understanding                                  |
| - Interpretable explanations at each level                        |
|                                                                   |
+------------------------------------------------------------------+
```

### Semantic Similarity Beyond Point Embeddings

Traditional approach vs. OT approach for code search:

```
Traditional (single embedding):
================================
query -> embed(query) -> v_q in R^384
code  -> embed(code)  -> v_c in R^384

similarity = cosine(v_q, v_c)

Problems:
- Long code compressed into single vector
- Query aspects may not all be captured
- No alignment explanation


OT approach (distribution matching):
====================================
query -> {embed(token_1), ..., embed(token_n)} = mu_q
code  -> {embed(token_1), ..., embed(token_m)} = mu_c

similarity = 1 / (1 + WMD(mu_q, mu_c))

Benefits:
- Preserves all query and code tokens
- Transport plan shows token-to-token alignment
- Handles variable-length inputs naturally
- Can weight tokens by importance (TF-IDF, position, etc.)
```

### Implementation Considerations for Code Search

| Aspect | Recommendation |
|--------|----------------|
| Embedding model | CodeBERT or E5 fine-tuned on code |
| Tokenization | BPE with identifier splitting |
| Weighting | Higher for signatures, lower for body |
| Distance | RWMD for fast filtering, exact WMD for top candidates |
| Indexing | Pre-compute RWMD lower bounds for pruning |

### OT-Based Reranking Pipeline

```
Query: "validate JWT token expiration"

Stage 1: Dense retrieval (fast, approximate)
==========================================
- Embed query as single vector
- ANN search returns top 100 candidates
- Latency: ~10ms

Stage 2: OT-based reranking (precise, interpretable)
===================================================
- Compute WMD between query and top 100 candidates
- Use RWMD for fast pruning
- Full WMD only for candidates passing RWMD threshold
- Return top 10 with transport plan alignment
- Latency: ~100ms

Output:
- validateToken() in JwtService.cs (WMD: 0.15)
  Alignment: validate<->validate, JWT<->jwt, token<->token,
             expiration<->exp

- checkTokenExpiry() in AuthHelper.cs (WMD: 0.22)
  Alignment: validate<->check, JWT<->(implicit),
             token<->Token, expiration<->Expiry
```

---

## References

### Foundational Books

1. **Villani, C. (2003)**. "Topics in Optimal Transportation." Graduate Studies in Mathematics, Vol. 58. American Mathematical Society.
   - [AMS Bookstore](https://bookstore.ams.org/gsm-58)
   - First comprehensive introduction; covers theory and applications

2. **Villani, C. (2008)**. "Optimal Transport: Old and New." Grundlehren der mathematischen Wissenschaften, Vol. 338. Springer.
   - [Springer Link](https://link.springer.com/book/10.1007/978-3-540-71050-9)
   - Encyclopedic reference; complete proofs; Fields Medal-winning work

3. **Peyre, G., & Cuturi, M. (2019)**. "Computational Optimal Transport." Foundations and Trends in Machine Learning, 11(5-6), 355-607.
   - [arXiv](https://arxiv.org/abs/1803.00567)
   - [Companion Website](https://optimaltransport.github.io/)
   - Modern computational focus; ML applications; code available

### Key Papers

4. **Cuturi, M. (2013)**. "Sinkhorn Distances: Lightspeed Computation of Optimal Transport." NeurIPS 2013.
   - [NeurIPS](https://papers.nips.cc/paper/4927-sinkhorn-distances-lightspeed-computation-of-optimal-transport)
   - [arXiv](https://arxiv.org/abs/1306.0895)
   - Introduced entropic regularization; made OT practical for ML

5. **Kusner, M. J., Sun, Y., Kolkin, N. I., & Weinberger, K. Q. (2015)**. "From Word Embeddings To Document Distances." ICML 2015.
   - [PMLR](https://proceedings.mlr.press/v37/kusnerb15.html)
   - [PDF](https://proceedings.mlr.press/v37/kusnerb15.pdf)
   - Word Mover's Distance; RWMD approximation

6. **Chizat, L., Peyre, G., Schmitzer, B., & Vialard, F.-X. (2018)**. "Unbalanced Optimal Transport: Dynamic and Kantorovich Formulations." Journal of Functional Analysis, 274(11), 3090-3123.
   - [arXiv](https://arxiv.org/abs/1508.05216)
   - Unbalanced OT theory; Wasserstein-Fisher-Rao metric

7. **Alvarez-Melis, D., & Jaakkola, T. (2018)**. "Gromov-Wasserstein Alignment of Word Embedding Spaces." EMNLP 2018.
   - [ACL Anthology](https://aclanthology.org/D18-1214.pdf)
   - [arXiv](https://arxiv.org/abs/1809.00013)
   - Cross-lingual alignment without parallel data

8. **Memoli, F. (2011)**. "Gromov-Wasserstein Distances and the Metric Approach to Object Matching." Foundations of Computational Mathematics, 11(4), 417-487.
   - [PDF](https://media.adelaide.edu.au/acvt/Publications/2011/2011-Gromov%E2%80%93Wasserstein%20Distances%20and%20the%20Metric%20Approach%20to%20Object%20Matching.pdf)
   - Theoretical foundations of GW distance

### Sliced Wasserstein

9. **Bonneel, N., Rabin, J., Peyre, G., & Pfister, H. (2015)**. "Sliced and Radon Wasserstein Barycenters of Measures." Journal of Mathematical Imaging and Vision, 51(1), 22-45.
   - Introduced sliced Wasserstein for efficient computation

10. **Nadjahi, K., De Bortoli, V., Durmus, A., Badeau, R., & Simsekli, U. (2021)**. "Fast Approximation of the Sliced-Wasserstein Distance Using Concentration of Random Projections." NeurIPS 2021.
    - [arXiv](https://arxiv.org/abs/2106.15427)
    - Deterministic approximation using concentration of measure

### Wasserstein Barycenters

11. **Cuturi, M., & Doucet, A. (2014)**. "Fast Computation of Wasserstein Barycenters." ICML 2014.
    - [PMLR](https://proceedings.mlr.press/v32/cuturi14.html)
    - [arXiv](https://arxiv.org/abs/1310.4375)
    - Entropic regularization for barycenter computation

### Accelerated WMD

12. **Atasu, K., Parnell, T., et al. (2017)**. "Linear-Complexity Relaxed Word Mover's Distance with GPU Acceleration." NeurIPS 2017.
    - [arXiv](https://arxiv.org/abs/1711.07227)
    - O(n) complexity RWMD; GPU implementation

### NLP Applications

13. **Yokoi, S., Takahashi, R., Akama, R., Suzuki, J., & Inui, K. (2020)**. "Word Rotator's Distance." EMNLP 2020.
    - Variant using rotation instead of translation

14. **Zhao, W., Peyrard, M., Liu, F., Gao, Y., Meyer, C. M., & Eger, S. (2019)**. "MoverScore: Text Generation Evaluating with Contextualized Embeddings and Earth Mover Distance." EMNLP 2019.
    - WMD with contextualized embeddings (BERT)

### Tutorials and Surveys

15. **Figalli, A. (2017)**. "An Introduction to Optimal Transport and Wasserstein Gradient Flows." Lecture Notes.
    - [PDF](https://people.math.ethz.ch/~afigalli/lecture-notes-pdf/An-introduction-to-optimal-transport-and-Wasserstein-gradient-flows.pdf)
    - Mathematical introduction; gradient flows

16. **Williams, A. (2020)**. "A Short Introduction to Optimal Transport and Wasserstein Distance."
    - [Blog](https://alexhwilliams.info/itsneuronalblog/2020/10/09/optimal-transport/)
    - Accessible introduction with visualizations

### Software Libraries

17. **POT: Python Optimal Transport**
    - [Documentation](https://pythonot.github.io/)
    - [GitHub](https://github.com/PythonOT/POT)
    - Comprehensive OT library; Sinkhorn, GW, unbalanced

18. **Gensim WMD Implementation**
    - [Tutorial](https://radimrehurek.com/gensim/auto_examples/tutorials/run_wmd.html)
    - WMD with Word2Vec embeddings

19. **OTT-JAX: Optimal Transport Tools in JAX**
    - [GitHub](https://github.com/ott-jax/ott)
    - GPU-accelerated; differentiable; modern implementation

### Industry Applications

20. **Google Research**. "Computational Optimal Transport."
    - [Research Page](https://research.google/pubs/computational-optimal-transport/)
    - Overview of Google's OT research

---

*Document version: 1.0 | Last updated: January 2026*
