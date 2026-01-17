# Metric Learning and Embedding Geometry

Mathematical foundations of metric spaces, embedding geometry, and their application to representation learning. This document provides the theoretical underpinnings for understanding why and how embedding-based retrieval systems work.

## Table of Contents

1. [Overview](#overview)
2. [Metric Spaces](#metric-spaces)
3. [Embedding Spaces](#embedding-spaces)
4. [Distance and Similarity Functions](#distance-and-similarity-functions)
5. [Contrastive Learning Theory](#contrastive-learning-theory)
6. [Metric Learning Objectives](#metric-learning-objectives)
7. [Embedding Space Properties](#embedding-space-properties)
8. [Nearest Neighbor Theory](#nearest-neighbor-theory)
9. [Applications to Code Embeddings](#applications-to-code-embeddings)
10. [References](#references)

---

## Overview

### Why Geometry Matters for Embeddings

Vector embeddings transform discrete objects (text, code, images) into continuous vector spaces where geometric relationships encode semantic relationships. The geometry of these spaces fundamentally determines:

- **Retrieval quality**: How well similar items cluster together
- **Generalization**: Whether learned relationships transfer to unseen data
- **Computational efficiency**: How quickly we can find nearest neighbors
- **Interpretability**: Whether geometric operations have meaningful semantics

```
Semantic Space                    Embedding Space

  "authenticate"                     *  <- authenticate
  "validate"      -- Embedding -->   * *  <- validate, verify
  "verify"             Model
                                      *  <- authorization
  "authorization"
                                    * *  <- login, sign-in
  "login"
  "sign-in"

Similar concepts --> Close vectors
```

**Central Principle**: If we can construct an embedding space where semantic similarity corresponds to geometric proximity, then nearest neighbor search becomes semantic search.

### Historical Context

The mathematical foundations draw from multiple fields:

| Field | Contribution | Key Result |
|-------|--------------|------------|
| Functional Analysis | Metric spaces, Hilbert spaces | Distance axioms, inner products |
| Topology | Manifold theory | Intrinsic dimensionality |
| Random Matrix Theory | Johnson-Lindenstrauss lemma | Dimensionality reduction bounds |
| Information Theory | Mutual information | InfoNCE connection |
| Statistical Learning | k-NN consistency | Stone's theorem |

---

## Metric Spaces

### Definition and Axioms

A **metric space** is a pair (X, d) where X is a set and d: X x X -> R is a distance function satisfying:

**Metric Axioms**:
```
1. Non-negativity:     d(x, y) >= 0  for all x, y in X
2. Identity:           d(x, y) = 0   iff x = y
3. Symmetry:           d(x, y) = d(y, x)  for all x, y in X
4. Triangle inequality: d(x, z) <= d(x, y) + d(y, z)  for all x, y, z in X
```

**Geometric Intuition**:
```
         y
        /|\
       / | \
  d(x,y) |  d(y,z)
     /   |   \
    /    |    \
   x-----+-----z
      d(x,z)

Triangle inequality: The direct path is never longer than going through y
```

### Common Metric Examples

#### Euclidean Distance (L2)

The most familiar metric, corresponding to "straight-line" distance:

```
d_2(x, y) = sqrt(sum_i (x_i - y_i)^2) = ||x - y||_2

For x = (1, 2), y = (4, 6):
d_2(x, y) = sqrt((4-1)^2 + (6-2)^2) = sqrt(9 + 16) = 5
```

**Properties**:
- Rotation-invariant
- Isotropic (treats all directions equally)
- Natural for geometric problems
- Euclidean spaces satisfy strong structural properties

#### Manhattan Distance (L1)

Sum of absolute differences along each axis:

```
d_1(x, y) = sum_i |x_i - y_i|

For x = (1, 2), y = (4, 6):
d_1(x, y) = |4-1| + |6-2| = 3 + 4 = 7
```

**Properties**:
- More robust to outliers than L2
- Natural for grid-based problems
- Preferred when features have different units

#### Cosine Distance

Derived from cosine similarity, measures angular separation:

```
d_cos(x, y) = 1 - cos(x, y) = 1 - (x . y) / (||x|| ||y||)

For normalized vectors (||x|| = ||y|| = 1):
d_cos(x, y) = 1 - x . y
```

**Properties**:
- Magnitude-invariant (only considers direction)
- Range: [0, 2] for real vectors
- Standard for text embeddings
- **Note**: Cosine distance is a metric only on normalized vectors

#### Hamming Distance

Counts differing positions (for discrete/binary vectors):

```
d_H(x, y) = sum_i I(x_i != y_i)

For x = (1,0,1,1), y = (1,1,0,1):
d_H(x, y) = 0 + 1 + 1 + 0 = 2
```

**Properties**:
- Natural for binary codes, error correction
- Used in binary hashing for ANN
- Efficiently computable via XOR + popcount

### Comparison of Metrics

| Metric | Formula | Use Case | Complexity |
|--------|---------|----------|------------|
| Euclidean (L2) | sqrt(sum((x_i - y_i)^2)) | General geometry, clustering | O(d) |
| Manhattan (L1) | sum(abs(x_i - y_i)) | Sparse data, robust similarity | O(d) |
| Cosine | 1 - (x.y)/(norm(x)*norm(y)) | Text/code embeddings | O(d) |
| Hamming | sum(x_i != y_i) | Binary codes, LSH | O(d/64) with SIMD |
| Chebyshev (L_inf) | max(abs(x_i - y_i)) | Worst-case bounds | O(d) |

### Pseudo-Metrics and Relaxations

Sometimes the full metric axioms are too restrictive. **Pseudo-metrics** relax the identity axiom:

**Pseudo-metric**: d(x, y) = 0 does not imply x = y

**Example**: Cosine distance on unnormalized vectors can have d(x, 2x) = 0 even though x != 2x.

**Semi-metrics** relax the triangle inequality:

**Semi-metric**: May violate d(x, z) <= d(x, y) + d(y, z)

**Example**: Squared Euclidean distance d(x,y) = ||x-y||^2 is a semi-metric (used in k-means).

**Practical Implication**: Many embedding similarity measures are semi-metrics or pseudo-metrics. This affects:
- Transitivity of similarity judgments
- Validity of certain index structures
- Theoretical guarantees of algorithms

---

## Embedding Spaces

### The Manifold Hypothesis

**Central Claim**: High-dimensional real-world data (images, text, code) lies on or near a low-dimensional manifold embedded in the ambient space.

```
Ambient Space (high-dimensional)
+----------------------------------+
|                                  |
|    ~~~~~~                        |
|   /      \    <- Data manifold   |
|  |   *  * |       (low-dim)      |
|   \  ** /                        |
|    ~~~~~~                        |
|                                  |
|  * = data points                 |
+----------------------------------+
```

**Mathematical Statement**: Let X subset of R^D be a dataset. The manifold hypothesis states that X lies on or near a manifold M of dimension d << D.

**Why This Matters**:
1. Explains why deep learning works despite apparent dimensionality
2. Justifies dimensionality reduction techniques
3. Explains interpolation and generalization behavior
4. Suggests intrinsic complexity of the learning problem

**Evidence**:
- Images: 1000x1000 pixel images (10^6 dimensions) can be compressed to ~100 latent dimensions
- Text: Vocabulary of 50,000 tokens embeds into 384-1024 dimensions effectively
- The success of autoencoders, t-SNE, UMAP all rely on this hypothesis

### Intrinsic Dimensionality

The **intrinsic dimensionality** of a dataset is the minimum number of free parameters needed to describe it locally.

**Formal Definition**: For a d-dimensional manifold M embedded in R^D, every point has a neighborhood homeomorphic to R^d. The value d is the intrinsic dimension.

**Estimation Methods**:

1. **PCA-based**: Count eigenvalues above a threshold
```
Explained variance ratio:
Lambda_1, Lambda_2, ..., Lambda_D (sorted descending)
d_intrinsic = min k such that sum(Lambda_1...Lambda_k)/sum(all) > 0.95
```

2. **Maximum Likelihood Estimation (MLE)**:
```
d_MLE = -1 / (1/k * sum_j log(r_j / r_k))

where r_j is distance to j-th nearest neighbor
```

3. **Correlation Dimension**:
```
d_corr = lim(r->0) log(C(r)) / log(r)

where C(r) = fraction of pairs with distance < r
```

**Practical Values**:
| Domain | Ambient Dim | Intrinsic Dim | Ratio |
|--------|-------------|---------------|-------|
| MNIST digits | 784 | ~10-15 | 50-80x |
| Natural images | 10^6+ | 100-1000 | 1000x+ |
| Text (BERT) | 768 | 50-100 | 8-15x |
| Code (AST) | varies | 20-50 | varies |

### The Johnson-Lindenstrauss Lemma

The **Johnson-Lindenstrauss (JL) Lemma** provides theoretical guarantees for random projection dimensionality reduction.

**Theorem (Johnson-Lindenstrauss, 1984)**: For any 0 < epsilon < 1 and any set Q of n points in R^d, there exists a map f: R^d -> R^k with k = O(log(n)/epsilon^2) such that for all u, v in Q:

```
(1 - epsilon) ||u - v||^2 <= ||f(u) - f(v)||^2 <= (1 + epsilon) ||u - v||^2
```

**Explicit Bound**: k >= 8 * ln(n) / epsilon^2 suffices.

**Construction**: The mapping can be realized by:
```
f(x) = (1/sqrt(k)) * A * x

where A is a k x d matrix with entries:
- Gaussian: A_ij ~ N(0, 1)
- Sparse: A_ij in {-1, 0, +1} with probabilities {1/6, 2/3, 1/6}
```

**Geometric Intuition**:
```
Original Space (d-dimensional)        Projected Space (k-dimensional)

     *                                      *
    / \                                    /|\
   /   \   -- Random Projection -->       / | \
  *-----*                                *--+--*

Distances preserved within (1 +/- epsilon) factor
```

**Key Properties**:

1. **Dimension-Independent**: Target dimension k depends only on n (number of points) and epsilon (distortion), not on original dimension d

2. **Data-Agnostic**: Projection matrix depends only on n, d, not on actual data values

3. **Probabilistic**: Guarantees hold with high probability (can be made arbitrarily high)

**Practical Implications**:
- Justifies dimensionality reduction for nearest neighbor search
- Explains why 384-dimensional embeddings can represent complex semantics
- Random projections can substitute for learned projections with bounded error

**Limitations**:
- JL lemma applies to Euclidean distances specifically
- Does not extend directly to other norms (L1, L_infinity)
- Constant factors in k can be large for small epsilon

---

## Distance and Similarity Functions

### Cosine Similarity vs Euclidean Distance

**Cosine Similarity**:
```
cos(x, y) = (x . y) / (||x|| ||y||) = sum_i(x_i * y_i) / (sqrt(sum_i x_i^2) * sqrt(sum_i y_i^2))

Range: [-1, 1] for real vectors
       [0, 1] for non-negative vectors
```

**Euclidean Distance**:
```
d_E(x, y) = ||x - y|| = sqrt(sum_i (x_i - y_i)^2)

Range: [0, infinity)
```

**Relationship for Normalized Vectors**:

When ||x|| = ||y|| = 1 (unit vectors):
```
||x - y||^2 = ||x||^2 + ||y||^2 - 2(x . y)
            = 1 + 1 - 2 cos(x, y)
            = 2(1 - cos(x, y))

Therefore: d_E(x, y) = sqrt(2(1 - cos(x, y))) = sqrt(2) * sqrt(1 - cos(x, y))
```

**Practical Equivalence**: For L2-normalized embeddings, minimizing Euclidean distance is equivalent to maximizing cosine similarity.

**When to Use Each**:

| Situation | Preferred Metric | Reason |
|-----------|-----------------|--------|
| Text embeddings (E5, BGE) | Cosine | Models trained with cosine objective |
| Normalized embeddings | Either | Equivalent |
| Unnormalized embeddings | Depends | Check model documentation |
| Magnitude matters | Euclidean | Cosine ignores magnitude |
| Direction matters | Cosine | Magnitude-invariant |

### Mahalanobis Distance

**Mahalanobis distance** accounts for correlations and varying scales in the data:

```
d_M(x, y) = sqrt((x - y)^T M (x - y))

where M is a positive semi-definite matrix
```

**Special Cases**:
- M = I (identity): Reduces to Euclidean distance
- M = diag(1/sigma_i^2): Standardized Euclidean (scales by variance)
- M = Sigma^(-1) (inverse covariance): Classic Mahalanobis

**Geometric Interpretation**:
```
Euclidean: Unit ball is a sphere
           All directions equally weighted

Mahalanobis: Unit ball is an ellipsoid
             Directions scaled by M
             Accounts for correlations

+-----+          +----+
|  o  |    vs    ( o  )
+-----+          +----+
 Sphere          Ellipsoid
```

**Why Mahalanobis Matters for Metric Learning**:

The Mahalanobis distance can be written as:
```
d_M(x, y) = ||L(x - y)||_2

where M = L^T L (Cholesky decomposition)
```

This is equivalent to applying a **linear transformation L** to the data, then computing Euclidean distance. Metric learning algorithms learn this transformation L.

**Connection to Neural Networks**:

A neural network embedding followed by Euclidean distance is a **nonlinear generalization** of Mahalanobis distance:
```
d_neural(x, y) = ||f(x) - f(y)||_2

where f: R^d -> R^k is a learned nonlinear mapping
```

### Kernel Methods and Implicit Feature Spaces

**Kernel functions** compute inner products in implicit high-dimensional (possibly infinite) feature spaces.

**Definition**: A kernel k: X x X -> R is a function such that:
```
k(x, y) = <phi(x), phi(y)>

where phi: X -> H is a feature map into a Hilbert space H
```

**Mercer's Theorem**: A symmetric function k(x, y) is a valid kernel if and only if it is positive semi-definite:
```
For any finite set {x_1, ..., x_n} and coefficients {c_1, ..., c_n}:
sum_i sum_j c_i c_j k(x_i, x_j) >= 0
```

**Common Kernels**:

| Kernel | Formula | Feature Space Dim |
|--------|---------|-------------------|
| Linear | x . y | d (original) |
| Polynomial | (x . y + c)^p | O(d^p) |
| Gaussian (RBF) | exp(-gamma ||x-y||^2) | Infinite |
| Laplacian | exp(-gamma ||x-y||_1) | Infinite |

**The Kernel Trick**:
```
Instead of:                     Use:
1. Compute phi(x), phi(y)       1. Compute k(x, y) directly
2. Take inner product           (avoids explicit feature computation)

phi(x) may be infinite-dimensional, but k(x, y) is always a scalar
```

**Connection to Embeddings**:

Modern neural embeddings can be viewed as:
1. Explicitly computing a finite-dimensional phi(x)
2. Using dot product (linear kernel) for similarity

The advantage over kernel methods:
- Explicit embeddings enable ANN indexing
- Batch processing is more efficient
- Representations can be cached and reused

---

## Contrastive Learning Theory

### Triplet Loss Geometry

**Triplet loss** is a fundamental metric learning objective that shapes embedding space geometry.

**Setup**: Given a triplet (anchor, positive, negative):
- Anchor a: Reference sample
- Positive p: Similar to anchor (same class/meaning)
- Negative n: Dissimilar to anchor (different class/meaning)

**Loss Function**:
```
L_triplet = max(0, d(a, p) - d(a, n) + margin)

where:
- d(., .) is a distance function (usually Euclidean or cosine)
- margin (alpha) is a hyperparameter (typically 0.1-0.5)
```

**Geometric Interpretation**:
```
Before Training:           After Training:

    n                          n
    *                          *
   /                            \
  /                              \  d(a,n)
 /                                \
*---*                     *---*    > margin
a   p                     a   p
                          |---|
                          d(a,p)

Goal: d(a, n) > d(a, p) + margin
```

**Loss Regions**:
```
d(a,n) - d(a,p)
      |
      |  Easy negatives: Loss = 0
      |  (already well-separated)
      |
margin+---------------------------
      |  Semi-hard negatives: 0 < Loss < margin
      |  (within margin, most informative)
      |
    0 +---------------------------
      |  Hard negatives: Loss > margin
      |  (violated constraint)
      |
```

**Mining Strategies**:

| Strategy | Selection | Properties |
|----------|-----------|------------|
| Random | Any negative | Fast, but slow convergence |
| Hard | d(a, n) < d(a, p) | Aggressive, can be unstable |
| Semi-hard | d(a, p) < d(a, n) < d(a, p) + margin | Balanced, commonly used |

**Key Properties of Triplet Loss**:

1. **Relative Constraints**: Only requires ordering, not absolute distances
2. **Intra-class Tolerance**: Allows variation within a class (unlike contrastive loss)
3. **Margin Control**: Explicit separation between classes
4. **Sampling Dependent**: Quality depends heavily on mining strategy

### InfoNCE and Connection to Mutual Information

**InfoNCE** (Noise Contrastive Estimation) is the dominant contrastive loss for self-supervised learning.

**Setup**: Given a query q, one positive key k+, and N-1 negative keys {k_1^-, ..., k_{N-1}^-}:

**Loss Function**:
```
L_InfoNCE = -log( exp(q . k+ / tau) / (exp(q . k+ / tau) + sum_i exp(q . k_i^- / tau)) )

where tau is a temperature parameter
```

**Equivalently**:
```
L_InfoNCE = -log( exp(sim(q, k+) / tau) / sum_j exp(sim(q, k_j) / tau) )

This is a softmax cross-entropy over "which key is the positive?"
```

**Connection to Mutual Information**:

**Theorem** (Oord et al., 2018): InfoNCE provides a lower bound on mutual information:
```
I(X; Y) >= log(N) - L_InfoNCE

where N is the number of negative samples
```

**Intuition**: The model learns to distinguish the positive from N-1 negatives. If it can do this perfectly, the representations must capture at least log(N) bits of mutual information.

**Temperature Effects**:
```
tau (temperature)    Effect
------------------------------------------
Large (tau >> 1)     Softer distribution, more uniform attention
Small (tau << 1)     Sharper distribution, focuses on hardest negatives
tau = 1              Standard softmax
Typical: 0.07-0.5    Depends on task and embedding scale
```

**Negative Sample Size**:

| N (negatives) | Effect | Trade-off |
|---------------|--------|-----------|
| Small (64-256) | Coarse discrimination | Faster, less memory |
| Medium (1K-4K) | Good balance | Common in practice |
| Large (16K-64K) | Fine discrimination | Better quality, expensive |

**Recent Findings** (2024):
- Too few negatives: Weak uniformity, suboptimal representations
- Too many negatives: Gradient contamination from false negatives
- Optimal range depends on dataset and task

### Alignment and Uniformity on the Hypersphere

**Wang & Isola (2020)** decomposed contrastive learning into two key properties:

**Alignment**: Positive pairs should be close
```
L_align = E_{(x,y)~p_pos} [||f(x) - f(y)||^2]

Measures average distance between positive pairs
```

**Uniformity**: Embeddings should be uniformly distributed on the hypersphere
```
L_uniform = log E_{(x,y)~p_data} [exp(-2 ||f(x) - f(y)||^2)]

Measures how spread out the embeddings are
```

**Geometric Visualization**:
```
Good Embeddings:              Bad Embeddings:
(aligned + uniform)           (collapsed)

     *   *                        *****
   *       *                       *
  *    S    *                     *
   *       *
     *   *

Points spread uniformly         Points clustered
on hypersphere S                in narrow cone
Positive pairs close            All points close
```

**Key Theorem**: In the limit of infinite negative samples, InfoNCE optimizes:
```
L_InfoNCE approx L_align + L_uniform

(up to constants)
```

**Implications**:
1. Contrastive learning balances two competing objectives
2. Alignment alone leads to collapsed representations
3. Uniformity alone ignores semantic structure
4. The balance creates useful semantic spaces

**Hypersphere Geometry**:

Why embeddings concentrate on the unit hypersphere:
1. Most contrastive models L2-normalize outputs
2. Cosine similarity = dot product on unit sphere
3. Surface area of d-dimensional sphere grows with d
4. Enables uniform distribution without unbounded spread

---

## Metric Learning Objectives

### Siamese Networks

**Siamese networks** use twin networks with shared weights to learn similarity metrics.

**Architecture**:
```
Input x1 -----> [Encoder f]-----> f(x1) ---\
                  ^                         \
                  | (shared weights)         }--> Distance d(f(x1), f(x2))
                  v                         /
Input x2 -----> [Encoder f]-----> f(x2) ---/
```

**Contrastive Loss** (original Siamese formulation):
```
L_contrastive = (1-y) * d(x1, x2)^2 + y * max(0, margin - d(x1, x2))^2

where:
- y = 0 if x1, x2 are similar (same class)
- y = 1 if x1, x2 are dissimilar (different class)
```

**Key Properties**:
1. **Symmetry by Design**: d(f(x1), f(x2)) = d(f(x2), f(x1)) guaranteed
2. **Weight Sharing**: Ensures consistent embedding space
3. **Efficient Training**: Only need pairwise comparisons

**Historical Significance**:
- Introduced by Bromley et al. (1993) for signature verification
- Popularized by Koch et al. (2015) for one-shot learning
- Foundation for modern contrastive approaches

### Prototypical Networks

**Prototypical networks** (Snell, Swersky, & Zemel, 2017) learn embeddings where classification reduces to nearest-prototype lookup.

**Core Idea**: Each class is represented by a prototype (mean of embedded support examples).

**Algorithm**:
```
1. For each class c with support set S_c:
   Prototype: p_c = (1/|S_c|) * sum_{x in S_c} f(x)

2. For query x, classify using softmax over distances:
   P(y = c | x) = exp(-d(f(x), p_c)) / sum_j exp(-d(f(x), p_j))
```

**Geometric Interpretation**:
```
Embedding Space:

    Class 1 support: * * *
    Class 1 prototype: X (centroid)

    Class 2 support: o o o
    Class 2 prototype: O (centroid)

    Query: ?

    Classify to nearest prototype:

    * * * X         O o o o
           \       /
            \  ?  /
             \   /
              \ /

    ? is closer to X, so classify as Class 1
```

**Loss Function**:
```
L_proto = -log( exp(-d(f(x), p_y)) / sum_c exp(-d(f(x), p_c)) )

where y is the true class of query x
```

**Distance Function Choice**:

The original paper proved that **squared Euclidean distance** is optimal:
```
d(x, y) = ||x - y||^2
```

This creates **Bregman divergence** structure, which has theoretical advantages for prototype-based classification.

**Advantages**:
1. Simple, elegant formulation
2. No learning of prototype positions (computed from support)
3. Works well with few examples per class
4. Extends naturally to zero-shot learning

### Learning Mahalanobis Metrics

**Goal**: Learn a positive semi-definite matrix M such that Mahalanobis distance d_M(x, y) = sqrt((x-y)^T M (x-y)) respects semantic similarity.

**Equivalent Formulation** (via L = sqrt(M)):
```
d_M(x, y) = ||L(x - y)||_2

Learn L directly, which is numerically more stable
```

**Large Margin Nearest Neighbor (LMNN)**:

The most influential Mahalanobis metric learning algorithm.

**Objective**:
```
min_L sum_{i,j in target_neighbors} d_L(x_i, x_j)^2
      + c * sum_{i,j,l} hinge_loss(d_L(x_i, x_j)^2 - d_L(x_i, x_l)^2 + 1)

where:
- First term: Pull target neighbors close
- Second term: Push impostors away with margin
```

**Constraints**:
- M = L^T L must be positive semi-definite
- Optimization is a semidefinite program (SDP)

**Scalability Methods**:
| Method | Complexity | Approach |
|--------|------------|----------|
| Full SDP | O(n^3 d^3) | Exact, small datasets |
| Diagonal M | O(n d) | Feature scaling only |
| Low-rank L | O(n d k) | Project to k dimensions |
| Stochastic | O(batch_size * d^2) | Mini-batch gradient descent |

**Connection to Deep Learning**:

A neural network with a linear final layer can be seen as learning a nonlinear Mahalanobis metric:
```
d(x, y) = ||W * g(x) - W * g(y)||_2

where:
- g(.) is the nonlinear encoder (all but last layer)
- W is the final linear layer
- Together, f(x) = W * g(x) learns a rich metric
```

---

## Embedding Space Properties

### Isotropy and Anisotropy

**Isotropy**: Embeddings are uniformly distributed in all directions.
**Anisotropy**: Embeddings are concentrated in a narrow cone or subspace.

**Mathematical Definition**:

For embeddings {v_1, ..., v_n}, consider the covariance matrix:
```
C = (1/n) * sum_i (v_i - mu)(v_i - mu)^T

where mu = (1/n) * sum_i v_i is the mean
```

**Isotropy Score** (IsoScore):
```
IsoScore = min_i(lambda_i) / max_i(lambda_i)

where lambda_i are eigenvalues of C
IsoScore = 1: Perfect isotropy (sphere)
IsoScore -> 0: High anisotropy (pancake/needle)
```

**The Anisotropy Problem in Transformers**:

Research has shown that BERT, GPT-2, and other transformer embeddings are highly anisotropic:
```
Observation: Most embedding vectors fall within a narrow cone
             Cosine similarity between random pairs is high (~0.6-0.9)
             Upper layers more anisotropic than lower layers

Implication: High baseline similarity reduces discriminative power
```

**Visualization**:
```
Isotropic Space:              Anisotropic Space:

     *  *  *                        *****
    *      *                          *
   *        *                        *
   *        *                       *
    *      *
     *  *  *

Uniform spread                 Narrow cone
All directions used            Few directions dominate
```

**Causes of Anisotropy**:
1. Training dynamics (SGD encourages compression)
2. Attention mechanisms (contextualization concentrates)
3. Layer normalization (can induce directional bias)
4. Word frequency effects (common words dominate mean)

**Mitigation Strategies**:

| Method | Approach | Effectiveness |
|--------|----------|---------------|
| Centering | Subtract mean: v' = v - mu | Significant improvement |
| Whitening | Transform to identity covariance | Stronger improvement |
| Post-hoc normalization | L2 normalize embeddings | Simple, often sufficient |
| Contrastive fine-tuning | Train with uniformity objective | Best results |

**Controversy**: Recent work suggests anisotropy may not be purely harmful:
- Anisotropy may reflect meaningful structure
- Forcing isotropy can hurt downstream performance
- The relationship is task-dependent

### The Hubness Problem

**Hubness** is a phenomenon where some points appear as nearest neighbors much more frequently than others.

**Definition**: Let N_k(x) be the number of times point x appears among the k-nearest neighbors of all other points:
```
N_k(x) = |{y : x in kNN(y)}|

Hubs: Points with unusually high N_k
Antihubs: Points with N_k = 0 or very low N_k
```

**The Problem**:
```
Normal Distribution:          Hubness Distribution:

  |  ****                       |*
  | **  **                      |*
  |*      *                     |**
  |        *                    | ****
  +----------                   +----------
   N_k values                    N_k values

Expected: Bell curve            Observed: Heavy right tail
          around k                        (many hubs)
```

**Causes**:

1. **Distance Concentration**: In high dimensions, distances become similar
```
As d -> infinity:
  Var(||x - y||) / E[||x - y||] -> 0

All points appear "equidistant"
```

2. **Geometric Effect**: Points near the centroid become hubs
```
Centroid proximity -> shorter distances to most points -> hub status
```

**Impact on Retrieval**:

| Problem | Effect | Severity |
|---------|--------|----------|
| Hubs dominate results | Same items returned for many queries | High |
| Antihubs never retrieved | Relevant items systematically missed | High |
| False positives | Hubs may be semantically unrelated | Medium |
| Metric failure | k-NN loses discriminative power | High |

**Hubness Reduction Methods**:

1. **Local Scaling**:
```
d_LS(x, y) = d(x, y) / sqrt(d(x, kNN_k(x)) * d(y, kNN_k(y)))

Scales distances by local density
```

2. **Mutual Proximity**:
```
d_MP(x, y) = 1 - P(d(x,z) > d(x,y)) * P(d(y,z) > d(x,y))

Uses probability rather than distance
```

3. **Centering**:
```
x' = x - (1/n) * sum_j x_j

Reduces global hubness by removing mean
```

4. **Fractional Norms**:
```
d_p(x, y) = (sum_i |x_i - y_i|^p)^(1/p) for p < 1

Less susceptible to distance concentration
```

### Curse of Dimensionality

The **curse of dimensionality** refers to phenomena that arise in high-dimensional spaces that have no counterpart in low dimensions.

**Key Phenomena**:

1. **Volume Concentration**:
```
Volume of unit ball in d dimensions:
V_d = pi^(d/2) / Gamma(d/2 + 1)

V_10 approx 2.55
V_100 approx 10^(-40)

Most volume is near the surface, interior is "empty"
```

2. **Distance Concentration**:
```
For random points x, y uniformly distributed:

E[||x - y||] / sqrt(d) -> constant as d -> infinity
Var[||x - y||] / d -> 0 as d -> infinity

All pairwise distances become similar
```

3. **Sample Complexity**:
```
To maintain fixed density of samples:
n_required ~ c^d

where c > 1 is a constant
Exponential growth in required samples
```

**Impact on Nearest Neighbor Search**:

| Dimension | Effect on k-NN |
|-----------|----------------|
| Low (d < 10) | Works well, exact search feasible |
| Medium (10-100) | Still effective, ANN helpful |
| High (100-1000) | Distances concentrate, hubness emerges |
| Very High (1000+) | Nearest neighbor may be no more similar than average |

**Mitigation in Embedding Systems**:

1. **Dimensionality Reduction**: Use JL projections or learned compression
2. **Normalized Embeddings**: Reduce effective dimensionality to d-1 (sphere)
3. **Local Methods**: Focus on local neighborhood structure
4. **Intrinsic Dimension**: Real data often has low intrinsic dimension despite high ambient dimension

---

## Nearest Neighbor Theory

### k-NN Consistency

**Stone's Universal Consistency Theorem (1977)**:

**Theorem**: For data in R^d, the k-nearest neighbor classifier is universally consistent: its probability of error converges to the Bayes error rate, for any data distribution, if k = k_n satisfies:
```
1. k_n -> infinity as n -> infinity
2. k_n / n -> 0 as n -> infinity
```

**Meaning**: With enough data and properly chosen k, k-NN achieves the best possible error rate.

**Formal Statement**:
```
Let R_n be the risk (error probability) of k-NN with n training samples
Let R* be the Bayes risk (optimal possible risk)

Then: R_n -> R* almost surely as n -> infinity
```

**Rate of Convergence**:

For k-NN classification with optimal k, under smoothness assumptions:
```
R_n - R* = O(n^(-2/(d+2)))

where d is the dimension
```

This rate degrades exponentially with dimension (curse of dimensionality).

**Practical Choice of k**:
```
Rule of thumb: k = sqrt(n)

More refined: k = n^(2/(d+4)) (optimal for MSE in regression)

Cross-validation: Best practical approach
```

**Extensions Beyond R^d**:

Stone's theorem relies on Euclidean geometry. For general metric spaces:
- Consistency may fail for spaces with complex topology
- Recent work (2020s) extends to spaces with finite Nagata dimension
- Separable metric spaces with regularity conditions maintain consistency

### Cover Trees and Ball Trees

**Cover Trees** and **Ball Trees** are data structures for efficient nearest neighbor search in metric spaces.

#### Ball Trees

**Structure**: Binary tree where each node represents a ball (hypersphere) containing a subset of points.

```
Root: Ball containing all points
      /            \
   Ball_L         Ball_R
   /    \         /    \
 ...    ...     ...    ...
Leaves: Individual points or small groups
```

**Construction**:
1. Find a pivot point (centroid or random)
2. Compute radius containing all points
3. Recursively partition into child balls

**Query Algorithm**:
```python
def query_ball_tree(node, query, k, candidates):
    if is_leaf(node):
        candidates.add(node.points)
    else:
        # Prune if ball is entirely farther than k-th candidate
        if min_distance(query, node.ball) > candidates.k_th_distance():
            return  # Prune

        # Search closer child first
        closer, farther = order_children_by_distance(node, query)
        query_ball_tree(closer, query, k, candidates)
        query_ball_tree(farther, query, k, candidates)
```

**Complexity**:
| Operation | Average Case | Worst Case |
|-----------|--------------|------------|
| Construction | O(n log n) | O(n^2) |
| Query | O(log n) | O(n) |
| Space | O(n) | O(n) |

#### Cover Trees

**Structure**: Multi-level tree where each level represents a covering of points at a specific scale.

**Invariants**:
```
1. Nesting: C_i subset of C_{i-1} (higher levels are subsets)
2. Covering: Every point in C_{i-1} is within 2^i of some point in C_i
3. Separation: Points in C_i are at least 2^i apart
```

**Visualization**:
```
Level 3:    *-------------------*     (coarse, few points)
            |                   |
Level 2:    *-----*-----*-------*     (medium)
            |     |     |       |
Level 1:    *-*-*-*-*-*-*-*-*-*-*     (fine, all points)
```

**Key Property**: Complexity depends on **intrinsic dimension**, not ambient dimension.

**Complexity**:
| Operation | Complexity |
|-----------|------------|
| Construction | O(c^6 n log n) |
| Query | O(c^12 log n) |
| Space | O(n) |

where c is the **expansion constant** (measure of intrinsic dimension).

**Comparison**:

| Aspect | Ball Tree | Cover Tree |
|--------|-----------|------------|
| Dimension dependence | Ambient | Intrinsic |
| Best for | Low ambient dimension | High ambient, low intrinsic |
| Implementation | Simpler | More complex |
| Practical performance | Good up to ~20d | Good in metric spaces |

### Approximate Nearest Neighbor Guarantees

**Definition**: An algorithm provides (c, r)-ANN if, for query q with true nearest neighbor at distance r*, it returns a point at distance at most c * r*.

**LSH (Locality-Sensitive Hashing)**:

For (r, cr, p1, p2)-sensitive hash functions:
```
P(h(x) = h(y) | d(x,y) <= r) >= p1
P(h(x) = h(y) | d(x,y) >= cr) <= p2

Query complexity: O(n^rho) where rho = log(1/p1) / log(1/p2)
```

**HNSW Guarantees**:

HNSW provides probabilistic guarantees:
```
With high probability, HNSW returns the true nearest neighbor
Probability depends on:
- M (connections per node): Higher M -> higher recall
- efSearch (search beam width): Higher ef -> higher recall

Typical: 95%+ recall at 10x speedup over brute force
```

**Product Quantization**:

PQ provides bounded distortion:
```
For PQ with m subspaces, k centroids each:
Approximation error ~ O(d/m * D^2 / k^(2/d_sub))

where D is the data diameter, d_sub = d/m
```

**Practical Trade-offs**:

| Method | Recall@10 | Speedup | Memory |
|--------|-----------|---------|--------|
| Brute Force | 100% | 1x | 1x |
| HNSW (M=16) | 95-99% | 10-100x | 1.2x |
| IVF-PQ | 80-95% | 100-1000x | 0.1-0.3x |
| LSH | 70-90% | 10-50x | 2-5x |

---

## Applications to Code Embeddings

### Why Cosine Works for Normalized Embeddings

**Observation**: Modern embedding models (E5, BGE, CodeBERT) produce embeddings that are most effective with cosine similarity.

**Reasons**:

1. **Training Objective**: Contrastive losses optimize cosine similarity
```
InfoNCE uses: sim(q, k) = q . k / (||q|| ||k||) / tau

Directly optimizes cosine similarity
```

2. **L2 Normalization**: Most models normalize embeddings
```
f(x) := f(x) / ||f(x)||

This projects to unit hypersphere
On unit sphere: cos(a, b) = a . b
```

3. **Magnitude Invariance**: Text/code length shouldn't affect similarity
```
"authentication" and "authentication module"
should have similar embeddings regardless of token count
Cosine ignores magnitude differences
```

4. **Numerical Stability**: Bounded similarity scores
```
cos(a, b) in [-1, 1] always
No need for score calibration/normalization
```

**E5 Model Specifics**:

E5 (EmbEddings from bidirEctional Encoder rEpresentations) trains with:
```
1. Contrastive pre-training on 270M text pairs
2. In-batch negatives with large batch size (32K)
3. Temperature-scaled InfoNCE loss
4. L2-normalized outputs

Result: Embeddings optimized for cosine similarity retrieval
```

### Embedding Space Visualization

**t-SNE (t-distributed Stochastic Neighbor Embedding)**:

**Theory**: Maps high-dimensional data to 2D/3D while preserving local neighborhood structure.

**Algorithm**:
```
1. Compute pairwise similarities in high-D using Gaussian kernel:
   p_{j|i} = exp(-||x_i - x_j||^2 / 2*sigma_i^2) / sum_k exp(-||x_i - x_k||^2 / 2*sigma_i^2)

2. Compute pairwise similarities in low-D using t-distribution:
   q_{ij} = (1 + ||y_i - y_j||^2)^(-1) / sum_{k!=l} (1 + ||y_k - y_l||^2)^(-1)

3. Minimize KL divergence between P and Q:
   KL(P||Q) = sum_i sum_j p_{ij} log(p_{ij} / q_{ij})
```

**Perplexity Parameter**:
```
Perplexity = 2^(Shannon entropy of P_i)

Intuition: Effective number of neighbors
Typical range: 5-50
Lower perplexity: Focus on very local structure
Higher perplexity: Consider more global structure
```

**UMAP (Uniform Manifold Approximation and Projection)**:

**Theory**: Based on Riemannian geometry and algebraic topology.

**Key Differences from t-SNE**:
```
1. Assumes data lies on locally connected manifold
2. Constructs fuzzy simplicial set (topological representation)
3. Optimizes cross-entropy between high-D and low-D fuzzy sets
4. Uses different attractive/repulsive forces
```

**Comparison**:

| Aspect | t-SNE | UMAP |
|--------|-------|------|
| Speed | Slower | Faster |
| Global structure | Weaker preservation | Better preservation |
| Reproducibility | Stochastic | Deterministic with seed |
| Scalability | ~10K points | ~1M points |
| Theory | Probabilistic | Topological |

**Interpreting Visualizations**:

**Valid Interpretations**:
- Clusters indicate groups of similar embeddings
- Relative distances within a cluster are meaningful
- Separation between clusters indicates dissimilarity

**Invalid Interpretations**:
- Distances between clusters are not preserved
- Cluster sizes don't reflect true spreads
- Elongated clusters may be artifacts

### Fine-Tuning Embedding Geometry

**Why Fine-Tune?**

Pre-trained models (E5, BGE) are trained on general corpora. Domain-specific data may have:
- Different vocabulary (code identifiers, jargon)
- Different similarity notions (syntactic vs semantic)
- Different distributions (file types, languages)

**Fine-Tuning Effects on Geometry**:

```
Before Fine-Tuning:              After Fine-Tuning:
(general embedding space)        (domain-adapted space)

  code  text  docs               code
   *  *  *  *  *                  * * *
  *  *  *  *  *  *                * * *
   *  *  *  *  *                      text  docs
                                       * * * *
Uniform mixing                   Domain-specific clustering
```

**Contrastive Fine-Tuning**:

```
1. Generate domain-specific training pairs:
   - Positive: (function, docstring), (query, relevant code)
   - Negative: In-batch or hard-mined negatives

2. Fine-tune with InfoNCE:
   L = -log(exp(sim(q, k+)/tau) / sum exp(sim(q, k_j)/tau))

3. Result: Embeddings cluster by domain-relevant similarity
```

**Adapter-Based Fine-Tuning**:

```
Frozen Encoder --> Adapter Layer --> Output

Adapter: Low-rank transformation
- Adds ~1% parameters
- Preserves general knowledge
- Adapts to domain
```

**Evaluation Metrics for Fine-Tuned Embeddings**:

| Metric | What It Measures | Target |
|--------|------------------|--------|
| MRR@10 | Ranking quality | > 0.5 |
| Recall@10 | Coverage | > 0.7 |
| NDCG@10 | Graded relevance | > 0.6 |
| Alignment | Positive pair distance | Lower is better |
| Uniformity | Distribution spread | Lower is better |

---

## References

### Foundational Papers

1. **Johnson, W.B. and Lindenstrauss, J. (1984)**. [Extensions of Lipschitz mappings into a Hilbert space](https://www.ams.org/journals/conm/1984-026-00/). Contemporary Mathematics 26, 189-206. *The original JL lemma paper.*

2. **Stone, C.J. (1977)**. [Consistent Nonparametric Regression](https://www.jstor.org/stable/2958783). Annals of Statistics, 5, 595-645. *Universal consistency of k-NN.*

3. **Bromley, J. et al. (1993)**. Signature Verification using a "Siamese" Time Delay Neural Network. NIPS 1993. *Original Siamese networks.*

4. **Dasgupta, S. and Gupta, A. (2003)**. [An Elementary Proof of a Theorem of Johnson and Lindenstrauss](https://cseweb.ucsd.edu/~dasgupta/papers/jl.pdf). Random Structures & Algorithms, 22(1), 60-65.

### Contrastive Learning

5. **Oord, A. van den et al. (2018)**. [Representation Learning with Contrastive Predictive Coding](https://arxiv.org/abs/1807.03748). arXiv:1807.03748. *InfoNCE loss introduction.*

6. **Wang, T. and Isola, P. (2020)**. [Understanding Contrastive Representation Learning through Alignment and Uniformity on the Hypersphere](https://arxiv.org/abs/2005.10242). ICML 2020. *Alignment and uniformity decomposition.*

7. **Schroff, F., Kalenichenko, D., and Philbin, J. (2015)**. [FaceNet: A Unified Embedding for Face Recognition and Clustering](https://arxiv.org/abs/1503.03832). CVPR 2015. *Triplet loss for embeddings.*

### Metric Learning

8. **Koch, G., Zemel, R., and Salakhutdinov, R. (2015)**. [Siamese Neural Networks for One-shot Image Recognition](https://www.cs.cmu.edu/~rsalakhu/papers/oneshot1.pdf). ICML Deep Learning Workshop. *Siamese networks for one-shot learning.*

9. **Snell, J., Swersky, K., and Zemel, R. (2017)**. [Prototypical Networks for Few-shot Learning](https://arxiv.org/abs/1703.05175). NeurIPS 2017. *Prototype-based metric learning.*

10. **Weinberger, K.Q. and Saul, L.K. (2009)**. [Distance Metric Learning for Large Margin Nearest Neighbor Classification](https://jmlr.org/papers/v10/weinberger09a.html). JMLR 10, 207-244. *LMNN algorithm.*

### Embedding Space Analysis

11. **Ethayarajh, K. (2019)**. [How Contextual are Contextualized Word Representations?](https://arxiv.org/abs/1909.00512). EMNLP 2019. *Anisotropy in transformer embeddings.*

12. **Radovanovic, M. et al. (2010)**. [Hubs in Space: Popular Nearest Neighbors in High-Dimensional Data](https://www.jmlr.org/papers/v11/radovanovic10a.html). JMLR 11, 2487-2531. *The hubness phenomenon.*

13. **Aggarwal, C.C. et al. (2001)**. On the Surprising Behavior of Distance Metrics in High Dimensional Space. ICDT 2001. *Curse of dimensionality.*

### Data Structures

14. **Beygelzimer, A., Kakade, S., and Langford, J. (2006)**. [Cover Trees for Nearest Neighbor](https://www.cs.princeton.edu/courses/archive/spr05/cos598E/bib/covertree.pdf). ICML 2006. *Cover tree data structure.*

15. **Malkov, Y.A. and Yashunin, D.A. (2018)**. [Efficient and robust approximate nearest neighbor search using Hierarchical Navigable Small World graphs](https://arxiv.org/abs/1603.09320). IEEE TPAMI. *HNSW algorithm.*

### Visualization

16. **van der Maaten, L. and Hinton, G. (2008)**. [Visualizing Data using t-SNE](https://www.jmlr.org/papers/v9/vandermaaten08a.html). JMLR 9, 2579-2605. *t-SNE algorithm.*

17. **McInnes, L., Healy, J., and Melville, J. (2018)**. [UMAP: Uniform Manifold Approximation and Projection for Dimension Reduction](https://arxiv.org/abs/1802.03426). arXiv:1802.03426. *UMAP algorithm.*

### Text and Code Embeddings

18. **Wang, L. et al. (2022)**. [Text Embeddings by Weakly-Supervised Contrastive Pre-training](https://arxiv.org/abs/2212.03533). arXiv:2212.03533. *E5 embedding model.*

19. **Feng, Z. et al. (2020)**. [CodeBERT: A Pre-Trained Model for Programming and Natural Languages](https://arxiv.org/abs/2002.08155). EMNLP 2020. *CodeBERT for code embeddings.*

### Online Resources

- [scikit-learn: Random Projection](https://scikit-learn.org/stable/modules/random_projection.html)
- [scikit-learn: Manifold Learning](https://scikit-learn.org/stable/modules/manifold.html)
- [metric-learn: Distance Metric Learning](https://contrib.scikit-learn.org/metric-learn/)
- [Sentence Transformers Documentation](https://sbert.net/)
- [Understanding UMAP (Google PAIR)](https://pair-code.github.io/understanding-umap/)
- [How to Use t-SNE Effectively (Distill)](https://distill.pub/2016/misread-tsne/)

---

*Document version: 1.0 | Last updated: January 2026*
