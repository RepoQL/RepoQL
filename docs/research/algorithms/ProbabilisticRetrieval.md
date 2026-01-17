# Probabilistic Retrieval Models

Mathematical foundations of probabilistic information retrieval, from classic Bayesian models to modern neural approaches.

## Table of Contents

1. [Overview](#overview)
2. [Probability Ranking Principle](#probability-ranking-principle)
3. [Binary Independence Model](#binary-independence-model)
4. [BM25 Derivation](#bm25-derivation)
5. [Language Models for IR](#language-models-for-ir)
6. [Relevance Models](#relevance-models)
7. [Probabilistic Topic Models](#probabilistic-topic-models)
8. [Neural Probabilistic Models](#neural-probabilistic-models)
9. [Uncertainty in Retrieval](#uncertainty-in-retrieval)
10. [Applications to Code Search](#applications-to-code-search)
11. [References](#references)

---

## Overview

Probabilistic retrieval models provide a principled mathematical framework for ranking documents by their likelihood of relevance to a query. Unlike vector space models that rely on geometric similarity, probabilistic models treat relevance as a random variable and use probability theory to estimate the likelihood that a document satisfies an information need.

### Why Probability Theory?

```
+------------------------------------------------------------------+
|                  PROBABILISTIC IR FRAMEWORK                       |
+------------------------------------------------------------------+
|                                                                   |
|  Given: Query Q, Document D, Collection C                         |
|                                                                   |
|  Goal: Estimate P(Relevant | D, Q)                                |
|                                                                   |
|  Key Insight: Rank by probability of relevance,                   |
|               not geometric distance                              |
|                                                                   |
+------------------------------------------------------------------+

Vector Space:     doc · query / (|doc| · |query|)  -> geometric
Probabilistic:    P(R=1 | D, Q)                     -> principled
```

**Core Advantages of Probabilistic Models**:

| Advantage | Explanation |
|-----------|-------------|
| Principled ranking | Optimal under well-defined assumptions |
| Uncertainty quantification | Can express confidence in rankings |
| Composable | Probabilities combine via Bayes' rule |
| Interpretable | Parameters have probabilistic meaning |
| Extensible | Easy to incorporate new evidence |

### Historical Development

```
1960s: Maron & Kuhns - First probabilistic IR model
   |
   v
1976: Robertson & Sparck Jones - Binary Independence Model
   |
   v
1977: Robertson - Probability Ranking Principle
   |
   v
1994: Robertson & Walker - BM25 (2-Poisson derivation)
   |
   v
1998: Ponte & Croft - Language Model approach
   |
   v
2001: Lavrenko & Croft - Relevance Models
   |
   v
2003: Blei, Ng, Jordan - Latent Dirichlet Allocation
   |
   v
2013+: Neural probabilistic models (Word2Vec, BERT, etc.)
```

### Notation Conventions

Throughout this document, we use the following notation:

| Symbol | Meaning |
|--------|---------|
| D | A document |
| Q | A query |
| t, w | A term (word) |
| R | Random variable for relevance (R=1 relevant, R=0 not) |
| C | Document collection (corpus) |
| N | Total number of documents in C |
| tf(t,D) | Term frequency: count of term t in document D |
| df(t) | Document frequency: number of documents containing t |
| P(·) | Probability |
| P(·\|·) | Conditional probability |

---

## Probability Ranking Principle

The Probability Ranking Principle (PRP), formulated by Robertson in 1977, provides the theoretical foundation for probabilistic IR. It states that optimal retrieval is achieved by ranking documents in decreasing order of their probability of relevance.

### Robertson's PRP Statement

> "If a system's response to each query is a ranking of the documents in the collection in order of decreasing probability of relevance to the query, where the probabilities are estimated as accurately as possible on the basis of whatever data have been made available to the system for this purpose, the overall effectiveness of the system to its user will be the best that is obtainable on the basis of those data."

### Formal Definition

Given a query Q and collection C = {D_1, D_2, ..., D_N}, rank documents such that:

```
If P(R=1 | D_i, Q) > P(R=1 | D_j, Q), then rank(D_i) < rank(D_j)
```

That is, documents with higher relevance probability appear earlier in the ranking.

### Optimality Under Uncertainty

The PRP is optimal in the sense that it minimizes expected loss. Consider a retrieval scenario with costs:

```
C_1 = Cost of not retrieving a relevant document (false negative)
C_0 = Cost of retrieving a non-relevant document (false positive)
```

**Decision Rule**: Retrieve document D if and only if:

```
C_0 · P(R=0 | D, Q) - C_1 · P(R=1 | D, Q) <= threshold
```

Rearranging using P(R=0|D,Q) = 1 - P(R=1|D,Q):

```
P(R=1 | D, Q) >= C_0 / (C_0 + C_1)
```

When costs are equal (C_0 = C_1), retrieve if P(R=1|D,Q) >= 0.5.

### Proof of Optimality (Sketch)

**Theorem**: Under the PRP, the expected loss is minimized.

**Proof Sketch**:

Let L be the expected loss for retrieving documents at positions 1 through k:

```
L = SUM_{i=1}^{k} [C_0 · P(R=0 | D_i, Q)] + SUM_{i=k+1}^{N} [C_1 · P(R=1 | D_i, Q)]
```

For any swap of documents D_i (retrieved) and D_j (not retrieved) where P(R=1|D_i,Q) < P(R=1|D_j,Q):

```
Change in loss = C_0 · [P(R=0|D_j,Q) - P(R=0|D_i,Q)] + C_1 · [P(R=1|D_i,Q) - P(R=1|D_j,Q)]
               = (C_0 + C_1) · [P(R=1|D_i,Q) - P(R=1|D_j,Q)]
               < 0  (since P(R=1|D_i,Q) < P(R=1|D_j,Q))
```

Therefore, swapping improves the loss, contradicting PRP ordering. QED.

### Limitations of the PRP

| Limitation | Description |
|------------|-------------|
| Independence assumption | Documents ranked independently; ignores diversity |
| Perfect probability estimation | Assumes accurate P(R=1\|D,Q) estimation |
| Single query | Doesn't optimize across query sessions |
| Binary relevance | Original formulation assumes binary relevance |

**Modern Extensions**:
- Risk-aware ranking (variance in relevance)
- Diversified ranking (novelty and coverage)
- Multi-graded relevance

---

## Binary Independence Model

The Binary Independence Model (BIM), developed by Robertson and Sparck Jones (1976), operationalizes the PRP by making specific assumptions about term distributions. It is the foundation for the RSJ (Robertson-Sparck Jones) weighting scheme.

### Model Assumptions

1. **Binary term representation**: Documents and queries are represented as binary vectors indicating term presence/absence
2. **Term independence**: Terms occur independently in relevant and non-relevant documents
3. **Relevance independence**: A document's relevance is independent of other documents' relevance

```
Document D:  [1, 0, 1, 1, 0, 0, 1, ...]   (1 = term present, 0 = absent)
Query Q:     [1, 0, 1, 0, 0, 0, 1, ...]
```

### Naive Bayes Derivation

Using Bayes' theorem to estimate relevance probability:

```
P(R=1 | D, Q) = P(D | R=1, Q) · P(R=1 | Q) / P(D | Q)
```

For ranking, we can ignore P(D|Q) and P(R=1|Q) as they're constant across documents. We use the odds ratio:

```
O(R | D, Q) = P(R=1 | D, Q) / P(R=0 | D, Q)
            = P(D | R=1, Q) · P(R=1 | Q) / [P(D | R=0, Q) · P(R=0 | Q)]
```

### Independence Assumption

Under term independence, the document likelihood factors:

```
P(D | R, Q) = PROD_{t in V} P(x_t | R, Q)

where x_t = 1 if term t is in D, 0 otherwise
```

Let:
- p_t = P(x_t = 1 | R=1, Q) = probability term t appears in relevant documents
- q_t = P(x_t = 1 | R=0, Q) = probability term t appears in non-relevant documents

### Log-Odds Derivation

Taking the log of the odds ratio and considering only query terms:

```
log O(R | D, Q) = SUM_{t in Q, t in D} log[p_t(1-q_t) / q_t(1-p_t)] + constant
```

The term weight for each query term present in the document is:

```
w_t = log[p_t(1-q_t) / q_t(1-p_t)] = log[p_t / (1-p_t)] - log[q_t / (1-q_t)]
```

This is the **log-odds ratio**, comparing the odds of term occurrence in relevant vs. non-relevant documents.

### RSJ Weights

The RSJ (Robertson-Sparck Jones) weight, using relevance feedback information:

```
              (r_t + 0.5) / (R - r_t + 0.5)
w_t = log  ------------------------------------
              (n_t - r_t + 0.5) / (N - n_t - R + r_t + 0.5)

Where:
  N   = Total documents in collection
  R   = Number of known relevant documents
  n_t = Documents containing term t
  r_t = Relevant documents containing term t
  0.5 = Smoothing constant to avoid zero probabilities
```

### Without Relevance Information

When no relevance feedback is available (r_t = 0, R = 0), the RSJ weight simplifies to:

```
              (N - n_t + 0.5)
w_t = log  -------------------  ≈  log(N / n_t)
              (n_t + 0.5)
```

This is the **Inverse Document Frequency (IDF)** - rare terms get higher weights.

### BIM Scoring Function

The final BIM scoring function:

```
score(D, Q) = SUM_{t in Q AND t in D} w_t

            = SUM_{t in Q AND t in D} log[(r_t + 0.5)(N - n_t - R + r_t + 0.5)]
                                          [(n_t - r_t + 0.5)(R - r_t + 0.5)]
```

### Contingency Table Interpretation

For each term t:

```
                    Relevant    Non-Relevant    Total
                   ---------   -------------   -------
Term present          r_t        n_t - r_t       n_t
Term absent         R - r_t    N-n_t-R+r_t     N - n_t
                   ---------   -------------   -------
Total                  R          N - R          N
```

The RSJ weight measures how much the term's presence increases the odds of relevance.

### Limitations of BIM

| Limitation | Impact | Solution |
|------------|--------|----------|
| Binary term weights | Ignores term frequency | Extended to 2-Poisson model |
| Independence assumption | Unrealistic for natural language | Relaxed in later models |
| Document length ignored | Longer documents unfairly favored | BM25 adds normalization |
| Requires relevance judgments | Not always available | Use IDF approximation |

---

## BM25 Derivation

BM25 (Best Matching 25) extends the Binary Independence Model by incorporating term frequency through the 2-Poisson model and adding document length normalization. It is one of the most successful retrieval functions ever developed.

### From Binary to Term Frequency

The BIM treats terms as binary (present/absent). To incorporate term frequency, Robertson and Walker (1994) introduced the **eliteness** concept via the 2-Poisson model.

### The 2-Poisson Model

**Key Idea**: Term occurrences in documents follow one of two Poisson distributions depending on whether the document is "elite" for that term (i.e., the term is topically central to the document).

```
Elite documents:      tf ~ Poisson(lambda_E)     (high mean)
Non-elite documents:  tf ~ Poisson(lambda_NE)    (low mean)

Relevant documents more likely to be elite for query terms.
```

```
+--------------------------------------------------+
|           2-POISSON MODEL INTUITION              |
+--------------------------------------------------+
|                                                  |
|  Term frequency distribution is bimodal:         |
|                                                  |
|  Frequency                                       |
|     ^                                            |
|     |    *                                       |
|     |   * *         *                            |
|     |  *   *       * *                           |
|     | *     *     *   *                          |
|     |*       *   *     *                         |
|     +----+----+----+----+-----> tf               |
|        Low TF      High TF                       |
|     (non-elite)    (elite)                       |
|                                                  |
+--------------------------------------------------+
```

### Term Frequency Saturation

A key insight from the 2-Poisson model: the contribution of term frequency to relevance should **saturate**. The 200th occurrence of a term doesn't double relevance compared to the 100th.

**Saturation Function**:

```
                tf
f(tf) = -------------------
         tf + k

As tf -> infinity, f(tf) -> 1 (saturation)
```

The parameter k controls the saturation rate:
- Small k: Quick saturation (early diminishing returns)
- Large k: Slow saturation (term frequency matters more)

```
f(tf) with different k values:

1.0 |                    -------- (k=0.5, fast saturation)
    |              ------
    |        ------     --------- (k=1.2, moderate)
0.5 |  ------
    | /     ---------------------  (k=2.0, slow saturation)
    |/
0.0 +----+----+----+----+----+-----> tf
    0    2    4    6    8    10
```

### Document Length Normalization

Longer documents naturally have more term occurrences. Without normalization, they would unfairly dominate rankings.

**Normalization Factor**:

```
B = 1 - b + b · (|D| / avgdl)

Where:
  |D|   = Document length (in terms)
  avgdl = Average document length in collection
  b     = Normalization parameter [0, 1]
```

- b = 0: No length normalization
- b = 1: Full normalization (scale as if all documents had average length)
- b = 0.75: Typical default (partial normalization)

### The BM25 Formula

Combining saturation with length normalization:

```
                                   tf(t, D) · (k_1 + 1)
BM25(D, Q) = SUM      IDF(t) · ---------------------------------
             t in Q               tf(t, D) + k_1 · B

Where:
  B = 1 - b + b · (|D| / avgdl)

  IDF(t) = log[(N - df(t) + 0.5) / (df(t) + 0.5)]
```

### Parameter Interpretation

| Parameter | Typical Value | Interpretation |
|-----------|---------------|----------------|
| k_1 | 1.2 - 2.0 | Term frequency saturation. Higher = tf matters more. |
| b | 0.75 | Length normalization. 0 = none, 1 = full. |

**Effect of k_1**:

```
k_1 = 0:   score = IDF only (binary model)
k_1 = inf: score = IDF · tf (linear in tf, no saturation)
k_1 = 1.2: balanced saturation (typical)
```

**Effect of b**:

```
b = 0:   All documents treated as same length
b = 0.5: Moderate normalization
b = 1.0: Full normalization (favors shorter documents)
```

### BM25 Component Breakdown

```
+------------------------------------------------------------+
|                    BM25 SCORE COMPONENTS                    |
+------------------------------------------------------------+
|                                                             |
|  For each query term t in document D:                       |
|                                                             |
|  +----------------+     +------------------+                |
|  |  IDF(t)        |  x  |  TF Saturation   |                |
|  |                |     |                  |                |
|  |  Measures term |     |  tf · (k1 + 1)   |                |
|  |  rarity in     |     |  -------------   |                |
|  |  collection    |     |  tf + k1 · B     |                |
|  +----------------+     +------------------+                |
|          |                      |                           |
|          |     +----------------+                           |
|          |     |                                            |
|          v     v                                            |
|    +------------------+                                     |
|    | Term Score       |                                     |
|    +------------------+                                     |
|             |                                               |
|   SUM over all query terms -> Document Score                |
+------------------------------------------------------------+
```

### BM25 Variants

| Variant | Modification | Use Case |
|---------|-------------|----------|
| BM25 | Original algorithm | General text retrieval |
| BM25F | Field-weighted tf | Structured documents |
| BM25+ | Lower bound on tf contribution | Long documents |
| BM25L | Modified length normalization | Variable-length documents |
| BM25-adpt | Adaptive parameters per query | Query-dependent tuning |

### BM25F for Structured Documents

For documents with multiple fields (title, body, etc.):

```
                              tf_weighted(t, D) · (k_1 + 1)
BM25F(D, Q) = SUM    IDF(t) · --------------------------------
              t in Q            k_1 + tf_weighted(t, D)

Where:
  tf_weighted(t, D) = SUM    w_f · tf(t, D, f) / B_f
                      f in fields

  B_f = 1 - b_f + b_f · (|D_f| / avgdl_f)
```

Each field f has its own weight w_f and normalization parameter b_f.

---

## Language Models for IR

Language modeling approaches to IR, pioneered by Ponte and Croft (1998), offer an alternative probabilistic framework. Instead of estimating P(R=1|D,Q), they estimate P(Q|D) - the probability of generating the query from a document's language model.

### Core Intuition

```
+------------------------------------------------------------------+
|              LANGUAGE MODEL INTUITION                             |
+------------------------------------------------------------------+
|                                                                   |
|  "If a user has an information need expressed by query Q,         |
|   what is the probability that they would generate Q              |
|   if document D perfectly satisfied their need?"                  |
|                                                                   |
|  High P(Q|D) suggests D is relevant to Q.                         |
|                                                                   |
+------------------------------------------------------------------+
```

### Query Likelihood Model

The dominant language model approach ranks documents by **query likelihood**:

```
score(D, Q) = P(Q | theta_D)
```

where theta_D is the language model estimated from document D.

Under term independence (unigram model):

```
P(Q | theta_D) = PROD_{t in Q} P(t | theta_D)

log P(Q | theta_D) = SUM_{t in Q} log P(t | theta_D)
```

### Maximum Likelihood Estimation

The simplest estimate for P(t|theta_D):

```
P_MLE(t | D) = tf(t, D) / |D|
```

**Problem**: If a query term doesn't appear in D, P(Q|D) = 0!

### The Smoothing Problem

Language model smoothing addresses two issues:
1. **Data sparseness**: Documents are small samples; unseen terms need non-zero probability
2. **Query modeling**: Common words in queries (e.g., "the") shouldn't dominate

### Jelinek-Mercer Smoothing

Linear interpolation with collection language model:

```
P_JM(t | D) = (1 - lambda) · P_MLE(t | D) + lambda · P(t | C)

Where:
  lambda = Smoothing parameter [0, 1]
  P(t | C) = cf(t) / |C| = Collection frequency
```

**Interpretation**: With probability (1-lambda), the term came from the document; with probability lambda, from the collection.

### Dirichlet Smoothing

Bayesian smoothing with Dirichlet prior:

```
                tf(t, D) + mu · P(t | C)
P_Dir(t | D) = --------------------------
                     |D| + mu

Where:
  mu = Dirichlet prior parameter (typically 1000-2000)
```

**Key Property**: Smoothing is document-length dependent. Longer documents need less smoothing (more data, more reliable estimates).

```
                 Effective smoothing
mu=2000:    <-----------+------------>
                  Short docs  Long docs
                  (more)      (less)
```

### Comparison: JM vs Dirichlet

| Aspect | Jelinek-Mercer | Dirichlet |
|--------|---------------|-----------|
| Smoothing strength | Constant (lambda) | Varies with |D| |
| Parameter | lambda (typically 0.1-0.7) | mu (typically 1000-2000) |
| Length normalization | None inherent | Built-in |
| Query type | Better for long queries | Better for short queries |

### Retrieval Formula with Smoothing

For Dirichlet smoothing, the ranking function becomes:

```
log P(Q | D) = SUM_{t in Q} log P_Dir(t | D)

             = SUM_{t in Q} log[tf(t,D) + mu · P(t|C)] - |Q| · log(|D| + mu)
```

Since the second term is constant for fixed-length queries, we can simplify to:

```
score(D, Q) = SUM_{t in Q AND t in D} log[1 + tf(t,D) / (mu · P(t|C))]
```

This reveals an **IDF-like component**: terms with low P(t|C) (rare terms) contribute more.

### Document Likelihood Model

An alternative formulation ranks by P(D|Q) instead of P(Q|D):

```
P(D | Q) proportional to P(Q | D) · P(D)
```

This requires a document prior P(D). Options include:
- Uniform: P(D) = 1/N
- Length-based: P(D) proportional to |D|
- Authority-based: P(D) from PageRank or similar

### KL-Divergence Retrieval Model

A more general framework models both query and document as language models and measures their divergence:

```
score(D, Q) = -KL(theta_Q || theta_D)
            = -SUM_t P(t | theta_Q) · log[P(t | theta_Q) / P(t | theta_D)]
```

**Intuition**: Rank documents whose language model is closest to the query's language model.

This subsumes query likelihood when theta_Q is the empirical query distribution.

---

## Relevance Models

Relevance models, introduced by Lavrenko and Croft (2001), provide a principled approach to query expansion through pseudo-relevance feedback. They estimate a model of the "ideal" relevant document.

### The Relevance Model Concept

```
+------------------------------------------------------------------+
|                 RELEVANCE MODEL INTUITION                         |
+------------------------------------------------------------------+
|                                                                   |
|  Query Q represents an information need.                          |
|                                                                   |
|  The "relevance model" R is the language model of                 |
|  a hypothetical ideal relevant document.                          |
|                                                                   |
|  Estimating P(w | R) tells us which words are likely              |
|  to appear in relevant documents.                                 |
|                                                                   |
+------------------------------------------------------------------+
```

### RM1: Basic Relevance Model

The RM1 model estimates P(w|R) from the query and pseudo-relevant documents:

```
P(w | R) = SUM_{D in F} P(w | D) · P(D | Q) / SUM_{D' in F} P(D' | Q)
```

Where F is the set of feedback documents (top-k from initial retrieval).

**Estimation Procedure**:
1. Run initial retrieval with query Q
2. Take top-k documents as pseudo-relevant set F
3. For each word w in vocabulary, compute P(w|R) using feedback documents
4. Weight contributions by document's relevance to query

### RM3: Query-Interpolated Relevance Model

RM3 interpolates the relevance model with the original query model:

```
P(w | RM3) = lambda · P(w | R) + (1 - lambda) · P(w | Q)

Where:
  P(w | Q) = Original query term distribution
  lambda = Interpolation parameter (typically 0.5-0.8)
```

**Advantages of RM3 over RM1**:
- Preserves original query intent
- Reduces topic drift
- More stable performance

### Retrieval with Relevance Models

Once the relevance model is estimated, retrieval uses KL-divergence:

```
score(D, Q) = -KL(theta_RM3 || theta_D)
            = SUM_w P(w | RM3) · log P(w | D)
```

### RM3 Algorithm

```
Algorithm: RM3 Query Expansion

Input: Query Q, Collection C, Parameters k, n, lambda

1. Initial Retrieval:
   F = top-k documents from C ranked by P(Q | D)

2. Estimate Relevance Model:
   For each word w in vocabulary:
     P(w | R) = SUM_{D in F} P(w | D) · P(D | Q) / Z

3. Select Expansion Terms:
   E = top-n words by P(w | R)

4. Interpolate with Query:
   For each w in (Q union E):
     P(w | RM3) = lambda · P(w | R) + (1-lambda) · P(w | Q)

5. Re-rank:
   For each D in C:
     score(D) = SUM_w P(w | RM3) · log P(w | D)
   Return documents sorted by score

Output: Re-ranked document list
```

### Example: RM3 in Action

```
Original Query: "machine learning tutorial"

Initial top-5 documents (pseudo-relevant):
  D1: "Introduction to Machine Learning with Python"
  D2: "Deep Learning Tutorial for Beginners"
  D3: "Statistical Learning Methods"
  D4: "Neural Networks: A Comprehensive Guide"
  D5: "Supervised Learning Algorithms"

Estimated P(w | R) (top expansion terms):
  neural:     0.08
  deep:       0.07
  algorithms: 0.06
  python:     0.05
  supervised: 0.04

Expanded Query (RM3 with lambda=0.6):
  machine:    0.18
  learning:   0.22
  tutorial:   0.12
  neural:     0.05  <- expanded
  deep:       0.04  <- expanded
  algorithms: 0.04  <- expanded
```

### Theoretical Foundation

Relevance models connect to the probability ranking principle:

```
P(w | R) = P(w | Q is relevant)
         = SUM_D P(w | D) · P(D | Q is relevant)
         ≈ SUM_D P(w | D) · P(Q | D) · P(D) / P(Q)  (by Bayes)
```

The feedback documents F approximate the truly relevant set.

---

## Probabilistic Topic Models

Topic models discover latent thematic structure in document collections. While not traditionally used for direct retrieval, they provide valuable document representations and enable semantic matching.

### PLSA: Probabilistic Latent Semantic Analysis

Hofmann (1999) introduced PLSA as a probabilistic alternative to Latent Semantic Analysis.

**Generative Model**:

```
1. For each document D:
   - Draw topic mixture: P(z | D)

2. For each word position in D:
   - Draw topic: z ~ P(z | D)
   - Draw word: w ~ P(w | z)
```

**Joint Probability**:

```
P(D, w) = P(D) · SUM_z P(w | z) · P(z | D)
```

### PLSA Graphical Model

```
                 +-------+
                 |   D   |    (Document)
                 +---+---+
                     |
                     v
                 +-------+
              +--|  z_n  |--+  (Topic for word n)
              |  +-------+  |
              |             |
              v             v
          +-------+    +-------+
          |   w   |    |   V   |   (Word, Vocabulary)
          +-------+    +-------+

Plate notation: Repeated N times per document
```

### LDA: Latent Dirichlet Allocation

Blei, Ng, and Jordan (2003) extended PLSA by adding Dirichlet priors, creating LDA - a fully generative Bayesian model.

**Generative Process**:

```
1. For each topic k = 1, ..., K:
   - Draw word distribution: phi_k ~ Dirichlet(beta)

2. For each document D:
   - Draw topic distribution: theta_D ~ Dirichlet(alpha)
   - For each word position n:
     - Draw topic: z_n ~ Multinomial(theta_D)
     - Draw word: w_n ~ Multinomial(phi_{z_n})
```

**Key Difference from PLSA**: Document-topic distributions theta_D are drawn from a Dirichlet prior, not estimated as free parameters. This provides:
- Better generalization to new documents
- Fewer parameters (avoids overfitting)
- Principled handling of uncertainty

### LDA for Retrieval

LDA-based retrieval options:

**1. Topic-based similarity**:
```
sim(D, Q) = cosine(theta_D, theta_Q)
```

**2. Word probability under topic model**:
```
P(w | D) = SUM_z P(w | z) · P(z | D)
```

**3. Document generation probability**:
```
P(D | Q) proportional to PROD_{w in Q} [SUM_z P(w | z) · P(z | D)]
```

### Topic Models vs. Dense Retrieval

| Aspect | Topic Models (LDA) | Dense Retrieval (BERT) |
|--------|-------------------|------------------------|
| Representation | Sparse topic vector (K~100) | Dense embedding (~768) |
| Training | Unsupervised | Contrastive/supervised |
| Interpretability | High (topics are word distributions) | Low (opaque vectors) |
| Semantic capture | Bag-of-words topics | Contextual semantics |
| Scalability | Excellent | Requires ANN indices |

### PLSA/LDA Limitations for IR

| Limitation | Impact |
|------------|--------|
| Bag-of-words | No word order or syntax |
| Fixed topic number K | Must be specified a priori |
| Training cost | Expensive for large corpora |
| Topic coherence | Topics may not align with queries |

---

## Neural Probabilistic Models

Modern neural approaches to IR can be viewed through a probabilistic lens. Understanding their connection to classic probabilistic models clarifies their training objectives and properties.

### Cross-Entropy Loss as Likelihood

Neural rankers are typically trained with cross-entropy loss, which is equivalent to maximum likelihood estimation:

```
L_CE = -log P(relevant | D, Q; theta)
     = -log sigmoid(f_theta(D, Q))
```

This maximizes the probability of the correct relevance label under a Bernoulli model.

### Contrastive Estimation

Contrastive learning trains models to distinguish positive pairs from negative pairs:

```
L_contrastive = -log[exp(sim(q, d+)) / (exp(sim(q, d+)) + SUM_{d-} exp(sim(q, d-)))]
```

This is equivalent to multi-class cross-entropy where the positive document is the correct class.

**Probabilistic Interpretation**:

```
P(d+ | q, {d+, d1-, d2-, ..., dk-}) = exp(sim(q, d+)) / SUM_d exp(sim(q, d))
```

The model learns to maximize the probability of selecting the positive document.

### InfoNCE Loss

The InfoNCE (Information Noise Contrastive Estimation) loss, widely used in contrastive learning:

```
L_InfoNCE = -log[exp(sim(q, d+)/tau) / SUM_{d in batch} exp(sim(q, d)/tau)]
```

Where tau is a temperature parameter controlling the sharpness of the distribution.

**Theoretical Properties**:
- Lower bound on mutual information I(Q; D)
- tau -> 0: Hard selection (argmax)
- tau -> inf: Uniform distribution

### Noise Contrastive Estimation (NCE)

NCE, introduced by Gutmann and Hyvarinen (2010), converts density estimation into binary classification:

**Objective**: Distinguish real data from noise samples.

```
L_NCE = -[log sigmoid(s(d, q)) + k · E_{d' ~ p_noise}[log(1 - sigmoid(s(d', q)))]]

Where:
  s(d, q) = model score
  k = number of noise samples per positive
  p_noise = noise distribution (often uniform or unigram)
```

**Key Properties**:
- Avoids computing partition function (normalizing constant)
- Training time independent of vocabulary size
- Asymptotically unbiased

### Negative Sampling (Word2Vec)

Negative sampling in Word2Vec is a simplified form of NCE:

```
L_NEG = -[log sigmoid(w · c) + SUM_{w' ~ p_neg} log sigmoid(-w' · c)]

Where:
  w = target word embedding
  c = context embedding
  p_neg = negative sampling distribution (often unigram^0.75)
```

**Difference from NCE**: Negative sampling doesn't include the noise distribution in the score computation, making it not asymptotically unbiased for density estimation - but highly effective for learning representations.

### Dense Retrieval Training

Modern dense retrieval (DPR, E5, etc.) typically uses in-batch negatives:

```
For batch of (query, positive_doc) pairs:
  All other documents in batch serve as negatives

L = -SUM_i log[exp(q_i · d_i+) / SUM_j exp(q_i · d_j)]
```

**Hard Negative Mining**: Using BM25 or previous model iteration to find challenging negatives improves training:

```
Negatives from:
1. Random documents (easy)
2. BM25 top-k (harder)
3. Previous epoch's top-k (hardest)
```

### Probabilistic View of Neural Rankers

```
+------------------------------------------------------------------+
|           NEURAL RANKERS AS PROBABILISTIC MODELS                  |
+------------------------------------------------------------------+
|                                                                   |
|  Cross-Encoder:  P(rel | D, Q) = sigmoid(BERT([CLS]; Q, D))      |
|                                                                   |
|  Bi-Encoder:     P(D | Q) proportional to exp(E_q · E_d / tau)   |
|                                                                   |
|  Training:       Maximum likelihood via cross-entropy             |
|                  = Minimize KL(true distribution || model)        |
|                                                                   |
+------------------------------------------------------------------+
```

---

## Uncertainty in Retrieval

Quantifying uncertainty in relevance predictions is increasingly important for applications like RAG, where retrieval confidence affects downstream decisions.

### Sources of Uncertainty

```
+------------------------------------------------------------------+
|                 SOURCES OF RETRIEVAL UNCERTAINTY                  |
+------------------------------------------------------------------+
|                                                                   |
|  1. Aleatoric (Data) Uncertainty                                  |
|     - Inherent ambiguity in relevance                             |
|     - Multiple valid interpretations of query                     |
|     - Borderline relevance cases                                  |
|                                                                   |
|  2. Epistemic (Model) Uncertainty                                 |
|     - Limited training data                                       |
|     - Model capacity limitations                                  |
|     - Out-of-distribution queries                                 |
|                                                                   |
+------------------------------------------------------------------+
```

### Confidence Estimation

Relevance scores can be converted to confidence estimates:

**Cross-Encoder Scores**: Naturally calibrated (trained with cross-entropy):
```
confidence = sigmoid(score)
```

**Bi-Encoder Scores**: Require calibration (cosine similarity not probability):
```
confidence = calibration_function(cosine_similarity)
```

### Calibration

A model is **calibrated** if predicted probabilities match empirical frequencies:

```
Calibrated: When model predicts P(rel) = 0.7,
            ~70% of those predictions are actually relevant.
```

**Calibration Metrics**:
- Expected Calibration Error (ECE)
- Reliability diagrams

### Bayesian Approaches

Bayesian methods provide uncertainty estimates by maintaining distributions over model parameters:

```
P(rel | D, Q) = INTEGRAL P(rel | D, Q, theta) · P(theta | training data) d_theta
```

**Practical Approximations**:
1. **MC Dropout**: Multiple forward passes with dropout
2. **Deep Ensembles**: Multiple independently trained models
3. **Variational Inference**: Approximate posterior over weights

### Efficient Uncertainty for Retrieval

Recent work on uncertainty in deep retrieval models:

```
Monte Carlo Estimation:
  1. Run model T times with different dropout masks
  2. Compute mean and variance of scores
  3. High variance -> high uncertainty

Efficient Bayesian Framework:
  - Stochastic process on scoring function
  - Negligible computational overhead
  - Enables risk-aware reranking
```

### Applications of Uncertainty

| Application | How Uncertainty Helps |
|-------------|----------------------|
| RAG Systems | Filter low-confidence retrievals |
| Active Learning | Sample uncertain examples for labeling |
| Ensemble Fusion | Weight sources by confidence |
| User Feedback | Focus on uncertain cases |
| Threshold Setting | Calibrated cutoffs for precision |

### Rank Calibration

For ranking applications, we care about calibration of relative ordering, not absolute probabilities:

```
Rank-calibrated: If score(D_i) > score(D_j),
                 then P(D_i relevant) > P(D_j relevant)
```

Risk-aware reranking can use uncertainty to avoid confidently wrong rankings.

---

## Applications to Code Search

Probabilistic models have natural applications to code search, where the unique structure of code presents both challenges and opportunities.

### Code as a Probabilistic Language

Code can be modeled as generated from a probabilistic process:

```
+------------------------------------------------------------------+
|              CODE GENERATION AS LANGUAGE MODEL                    |
+------------------------------------------------------------------+
|                                                                   |
|  Natural Language:   P(w_t | w_1, ..., w_{t-1})                  |
|                                                                   |
|  Code:               P(token_t | context, syntax, semantics)      |
|                                                                   |
|  Key Differences:                                                 |
|    - Stronger syntactic constraints                               |
|    - Long-range dependencies (variable references)                |
|    - Multi-modal: identifiers + structure + comments              |
|                                                                   |
+------------------------------------------------------------------+
```

### BM25F for Code

BM25F's field weighting is particularly valuable for code, where different elements have different importance:

```
Field Weights for Code Search:
+------------------+--------+----------------------------------+
| Field            | Weight | Rationale                        |
+------------------+--------+----------------------------------+
| Function name    |   5.0  | Primary identifier               |
| Class name       |   4.0  | Key structural element           |
| Parameter names  |   3.0  | API signature                    |
| Return type      |   2.5  | Semantic information             |
| Documentation    |   2.0  | Intent description               |
| Body tokens      |   1.0  | Implementation details           |
+------------------+--------+----------------------------------+
```

### Identifier Tokenization for LM

Code identifiers require special handling for language models:

```
CamelCase:    getUserById   -> [get, User, By, Id]
snake_case:   get_user_by_id -> [get, user, by, id]
Abbreviations: XMLHTTPRequest -> [XML, HTTP, Request]

Token vocabulary must include:
  - Full identifiers (exact matching)
  - Subtokens (partial matching)
  - Common abbreviations
```

### Topic Models for Code

LDA-style topic models discover structural patterns in codebases:

```
Example Code Topics:
  Topic 1 (Database): query, select, insert, connection, cursor, commit
  Topic 2 (Auth):     user, token, password, session, login, authenticate
  Topic 3 (HTTP):     request, response, header, status, url, method
  Topic 4 (Testing):  test, assert, mock, expect, suite, fixture
```

Applications:
- Code clustering and organization
- Related code discovery
- API usage patterns

### Relevance Models for Code Expansion

RM3-style expansion adapts well to code:

```
Query: "parse JSON"

Pseudo-relevant code snippets provide expansion terms:
  decode:     0.12
  loads:      0.10
  dictionary: 0.08
  string:     0.07
  object:     0.06

Expanded query finds code using json.loads, JSON.parse, etc.
```

### Uncertainty in Code Search

Code search benefits from uncertainty estimation:

| Scenario | Uncertainty Signal | Action |
|----------|-------------------|--------|
| Ambiguous query | High uncertainty across results | Request clarification |
| Multiple implementations | High variance | Show diverse options |
| Exact match found | Low uncertainty, high confidence | Prioritize strongly |
| Out-of-vocabulary terms | High epistemic uncertainty | Fall back to fuzzy matching |

### Probabilistic Code Embeddings

Neural code models can be viewed probabilistically:

```
CodeBERT/GraphCodeBERT:
  P(code_context | query) estimated via dot-product in embedding space

Training:
  Maximize P(positive_code | query) vs P(negative_code | query)
  via contrastive learning

Inference:
  rank(D) = E_query · E_document  (log-probability proxy)
```

### Hybrid Probabilistic-Neural for Code

Combining probabilistic and neural approaches:

```
+-------------------+     +-------------------+     +------------------+
|   BM25F           |     |   Dense Retrieval |     |   Final Ranking  |
|   (Probabilistic) |     |   (Neural)        |     |                  |
|                   |     |                   |     |                  |
|   - Exact names   | --> |   - Semantic      | --> |   Fusion + LTR   |
|   - Rare tokens   |     |     similarity    |     |   with features  |
|   - Structured    |     |   - Intent        |     |   from both      |
+-------------------+     +-------------------+     +------------------+
```

This leverages the strengths of both paradigms:
- Probabilistic: Exact identifier matching, rare token handling, interpretability
- Neural: Semantic understanding, query-document gap bridging

---

## References

### Foundational Papers

1. **Robertson, S.E.** (1977). *The Probability Ranking Principle in IR*. Journal of Documentation, 33(4), 294-304.
   - [Emerald Publishing](https://www.emerald.com/insight/content/doi/10.1108/eb026647/full/html)

2. **Robertson, S.E. and Sparck Jones, K.** (1976). *Relevance Weighting of Search Terms*. Journal of the American Society for Information Science, 27(3), 129-146.

3. **Robertson, S.E. and Walker, S.** (1994). *Some Simple Effective Approximations to the 2-Poisson Model for Probabilistic Weighted Retrieval*. SIGIR '94.

4. **Robertson, S.E. and Zaragoza, H.** (2009). *The Probabilistic Relevance Framework: BM25 and Beyond*. Foundations and Trends in Information Retrieval, 3(4), 333-389.
   - [Paper PDF](https://www.staff.city.ac.uk/~sbrp622/papers/foundations_bm25_review.pdf)

### Language Models for IR

5. **Ponte, J.M. and Croft, W.B.** (1998). *A Language Modeling Approach to Information Retrieval*. SIGIR '98.

6. **Zhai, C. and Lafferty, J.** (2001). *A Study of Smoothing Methods for Language Models Applied to Ad Hoc Information Retrieval*. SIGIR '01.
   - [SIGIR Paper](https://sigir.org/wp-content/uploads/2017/06/p268.pdf)

7. **Zhai, C. and Lafferty, J.** (2004). *A Study of Smoothing Methods for Language Models Applied to Information Retrieval*. ACM TOIS, 22(2), 179-214.

### Relevance Models

8. **Lavrenko, V. and Croft, W.B.** (2001). *Relevance-Based Language Models*. SIGIR '01.
   - [ResearchGate](https://www.researchgate.net/publication/221299786_Relevance-based_language_models)

9. **Abdul-Jaleel, N., et al.** (2004). *UMass at TREC 2004: Novelty and HARD*. TREC '04.
   - Introduces RM3 query interpolation

### Topic Models

10. **Hofmann, T.** (1999). *Probabilistic Latent Semantic Analysis*. UAI '99.
    - [arXiv](https://arxiv.org/abs/1301.6705)

11. **Blei, D.M., Ng, A.Y., and Jordan, M.I.** (2003). *Latent Dirichlet Allocation*. Journal of Machine Learning Research, 3, 993-1022.
    - [JMLR Paper](https://www.jmlr.org/papers/volume3/blei03a/blei03a.pdf)

### Neural Probabilistic Models

12. **Gutmann, M.U. and Hyvarinen, A.** (2010). *Noise-Contrastive Estimation: A New Estimation Principle for Unnormalized Statistical Models*. AISTATS '10.

13. **Mnih, A. and Teh, Y.W.** (2012). *A Fast and Simple Algorithm for Training Neural Probabilistic Language Models*. ICML '12.
    - [NCE for Language Models](https://www.cs.toronto.edu/~amnih/papers/wordreps.pdf)

14. **Mikolov, T., et al.** (2013). *Distributed Representations of Words and Phrases and their Compositionality*. NeurIPS '13.
    - Word2Vec negative sampling

### Uncertainty in Retrieval

15. **Penha, G. and Hauff, C.** (2021). *Not All Relevance Scores are Equal: Efficient Uncertainty and Calibration Modeling for Deep Retrieval Models*. SIGIR '21.
    - [arXiv](https://arxiv.org/abs/2105.04651)

16. **Penha, G. and Hauff, C.** (2020). *On the Calibration and Uncertainty of Neural Learning to Rank Models for Conversational Search*. SIGIR '20.

### Code Search

17. **Gu, X., et al.** (2018). *Deep Code Search*. ICSE '18.
    - CODEnn neural code search

18. **Feng, Z., et al.** (2020). *CodeBERT: A Pre-Trained Model for Programming and Natural Languages*. EMNLP '20.

19. **Husain, H., et al.** (2019). *CodeSearchNet Challenge: Evaluating the State of Semantic Code Search*. arXiv:1909.09436.
    - [ResearchGate](https://www.researchgate.net/publication/335976202_CodeSearchNet_Challenge_Evaluating_the_State_of_Semantic_Code_Search)

### Textbooks and Surveys

20. **Manning, C.D., Raghavan, P., and Schutze, H.** (2008). *Introduction to Information Retrieval*. Cambridge University Press.
    - [Online Version](https://nlp.stanford.edu/IR-book/pdf/11prob.pdf) (Chapter 11: Probabilistic IR)
    - [Language Models Chapter](https://nlp.stanford.edu/IR-book/pdf/12lmodel.pdf)

21. **Croft, W.B., Metzler, D., and Strohman, T.** (2010). *Search Engines: Information Retrieval in Practice*. Pearson.

22. **CIIR Technical Reports**. Center for Intelligent Information Retrieval, UMass Amherst.
    - [Statistical Language Modeling for IR](https://ciir.cs.umass.edu/pubfiles/ir-318.pdf)

### Additional Resources

23. **IDF Derivation within RSJ Framework**
    - Lee, L. (2007). *IDF Revisited: A Simple New Derivation within the Robertson-Sparck Jones Probabilistic Model*.
    - [Cornell CS](https://www.cs.cornell.edu/home/llee/papers/idf.pdf)

24. **RSJ-PM Tutorial**
    - [University of Illinois Course Notes](http://times.cs.uiuc.edu/course/598f16/notes/rsj-derivation.pdf)

25. **KL-Divergence in IR**
    - [Semantic Scholar](https://www.semanticscholar.org/paper/Notes-on-the-KL-divergence-retrieval-formula-and-Zhai/fd973eba2e29fa94a2283c3fb29a3b8a14970a50)

---

*Document version: 1.0 | Last updated: January 2026*
