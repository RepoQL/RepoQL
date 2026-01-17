# Token-Budgeted Context Selection

Comprehensive documentation on selecting optimal evidence sets under token budget constraints using submodular optimization, MMR, and value-of-information frameworks.

## Table of Contents

1. [Overview: The Budget-Constrained Selection Problem](#overview-the-budget-constrained-selection-problem)
2. [Maximal Marginal Relevance (MMR)](#maximal-marginal-relevance-mmr)
3. [Submodular Functions and Summarization](#submodular-functions-and-summarization)
4. [Budgeted Maximum Coverage](#budgeted-maximum-coverage)
5. [Value of Information (VOI)](#value-of-information-voi)
6. [Utility Functions for Code Search](#utility-functions-for-code-search)
7. [Greedy Algorithms and Approximation Guarantees](#greedy-algorithms-and-approximation-guarantees)
8. [Implementation Strategies for DuckDB/SQL](#implementation-strategies-for-duckdbsql)
9. [Practical Algorithms for Agent Context Selection](#practical-algorithms-for-agent-context-selection)
10. [Best Practices and Common Pitfalls](#best-practices-and-common-pitfalls)
11. [References](#references)

---

## Overview: The Budget-Constrained Selection Problem

### The Core Challenge

When an agent queries a codebase, it faces a fundamental constraint: the context window is finite. Selecting the "top-k by relevance" is suboptimal because:

1. **Redundancy**: Top results often overlap semantically
2. **Coverage gaps**: Important aspects of the answer may be buried lower in rankings
3. **Variable costs**: Code snippets have different token lengths
4. **Diminishing returns**: The 10th most relevant result adds less value than the 1st

The problem is not "find the k most relevant items" but rather "find the best subset under a budget."

### Formal Problem Statement

```
Given:
  - Candidate set C = {c_1, c_2, ..., c_n}
  - Cost function cost(c_i) -> tokens
  - Utility function U(S) for subset S
  - Budget B (total tokens available)

Find:
  S* = argmax U(S)
       S in C
       subject to: SUM cost(c_i) <= B
                   i in S
```

### Why This Differs from Top-K

```
Top-K Selection                    Budgeted Selection
==================                 ====================

Query: "authentication"            Query: "authentication"
Budget: 2000 tokens                Budget: 2000 tokens

Top-3 by relevance:                Optimal budget allocation:
1. AuthService.cs (800 tok)        1. AuthService.cs (800 tok)
2. AuthServiceTests.cs (750 tok)   2. JwtValidator.cs (400 tok)
3. AuthServiceImpl.cs (700 tok)    3. AuthConfig.cs (350 tok)
                                   4. IAuthProvider.cs (200 tok)
Total: 2250 tokens (OVER!)         5. AuthErrors.cs (180 tok)
OR truncate to 2 items = 1550
                                   Total: 1930 tokens
Coverage: AuthService only         Coverage: Interface, impl,
                                   config, validation, errors
```

The budgeted approach achieves broader coverage within the same constraint.

---

## Maximal Marginal Relevance (MMR)

### Origin and Motivation

MMR was introduced by Carbonell and Goldstein in 1998 to address redundancy in document retrieval and summarization. The key insight: a document's value depends not only on its relevance to the query but also on what information has already been selected.

### The MMR Formula

```
MMR(d) = lambda * Sim(d, Q) - (1-lambda) * max Sim(d, d_j)
                                           d_j in S

Where:
  d      = candidate document
  Q      = query
  S      = already selected set
  lambda = tradeoff parameter [0, 1]
  Sim()  = similarity function (e.g., cosine similarity)
```

### Interpretation of Lambda

| Lambda Value | Behavior | Use Case |
|--------------|----------|----------|
| 1.0 | Pure relevance (no diversity) | Precision-critical search |
| 0.7-0.9 | High relevance, some diversity | Focused debugging |
| 0.5-0.7 | Balanced | Research/exploration |
| 0.3-0.5 | Diversity-emphasized | Broad coverage needed |
| 0.0 | Pure diversity (ignore relevance) | Never useful in practice |

### MMR Selection Algorithm

```
Algorithm: MMR_Select(C, Q, k, lambda)
============================================
Input:  C = candidates, Q = query, k = count, lambda = tradeoff
Output: S = selected set of size k

S <- empty set
while |S| < k and C not empty:
    for each d in C \ S:
        if S = empty:
            score[d] <- Sim(d, Q)
        else:
            relevance <- Sim(d, Q)
            redundancy <- max(Sim(d, s) for s in S)
            score[d] <- lambda * relevance - (1-lambda) * redundancy

    d* <- argmax(score)
    S <- S union {d*}

return S
```

### MMR for Code Search

For code retrieval, the similarity function can be decomposed:

```
Sim_code(d, Q) = w_1 * semantic_sim(embed(d), embed(Q))
              + w_2 * lexical_sim(tokens(d), tokens(Q))
              + w_3 * structural_sim(ast(d), intent(Q))
```

And the redundancy measure should consider:

```
Redundancy(d, S) = max( semantic_overlap(d, s),
                       symbol_overlap(d, s),
                       file_proximity(d, s) )
                  for s in S
```

---

## Submodular Functions and Summarization

### What Makes a Function Submodular?

A set function f: 2^V -> R is submodular if it exhibits **diminishing returns**:

```
For all A subset B subset V and x not in B:

f(A union {x}) - f(A) >= f(B union {x}) - f(B)

"Adding x to a smaller set A gives at least as much
 gain as adding x to a larger set B."
```

### Visual Intuition

```
Marginal Gain of Adding Item x
|
|    *  Adding x to empty set
|     \
|      \
|       *  Adding x to small set
|         \
|          \
|           *  Adding x to larger set
|             \____*  Adding x to big set
|
+-------------------------------------------> Size of existing set
```

### Lin-Bilmes Summarization Functions (ACL 2011)

Lin and Bilmes proposed a class of submodular functions for document summarization that combine coverage and diversity:

```
f(S) = L(S) + lambda * R(S)

Where:
  L(S) = Coverage term (how much of the corpus is "covered")
  R(S) = Diversity/reward term
  lambda = Tradeoff parameter
```

#### Coverage Function

```
L(S) = SUM min(SUM sim(i,j), alpha * SUM sim(i,j))
       i in V   j in S            j in V

"Each element i in the ground set contributes based on
 how well it's covered by selected items, capped at
 alpha times its total potential coverage."
```

#### Diversity Function

```
R(S) = SUM  SUM  sim(i,j)
       k   i,j in S intersect P_k

"Reward selecting diverse items from different partitions P_k"
```

### Why Submodularity Matters

Submodular functions have a remarkable property: the greedy algorithm achieves provably good results.

```
Theorem (Nemhauser, Wolsey, Fisher 1978):
For a monotone submodular function f with f(empty) = 0,
the greedy algorithm that iteratively adds the element
with maximum marginal gain achieves:

f(S_greedy) >= (1 - 1/e) * f(S_optimal) ~ 0.632 * OPT
```

This guarantee is **tight**: no polynomial algorithm can do better (unless P=NP).

---

## Budgeted Maximum Coverage

### Problem Definition

The budgeted maximum coverage problem generalizes maximum coverage to items with varying costs:

```
Given:
  - Universe U of elements to cover
  - Collection S = {S_1, ..., S_m} of subsets
  - Cost c_i for each subset S_i
  - Weight w_j for each element j in U
  - Budget B

Find:
  T subset S maximizing SUM w_j * [j covered by T]
                         j in U
  subject to: SUM c_i <= B
              S_i in T
```

### Mapping to Code Search

| Coverage Concept | Code Search Analog |
|-----------------|-------------------|
| Universe U | Concepts/topics the query needs |
| Subset S_i | Code snippet covering certain concepts |
| Cost c_i | Token count of snippet |
| Weight w_j | Importance of concept to query |
| Budget B | Context window allocation |

### Khuller-Moss-Naor Algorithm (1999)

```
Algorithm: Budgeted_Coverage(S, c, w, B)
============================================
Input:  S = subsets, c = costs, w = weights, B = budget
Output: Selected subsets T

# Greedy phase
T_greedy <- empty set
remaining <- B
while remaining > 0 and uncovered elements exist:
    for each S_i not in T_greedy with c_i <= remaining:
        efficiency[i] <- (new coverage weight) / c_i

    S* <- argmax(efficiency)
    T_greedy <- T_greedy union {S*}
    remaining <- remaining - c[S*]

# Enumeration phase (for approximation guarantee)
best <- T_greedy
for each subset S_i with high individual coverage:
    T_single <- {S_i}
    # Run greedy on remaining budget
    T_combined <- T_single union Greedy(remaining budget)
    if coverage(T_combined) > coverage(best):
        best <- T_combined

return best
```

### Approximation Guarantee

The Khuller-Moss-Naor algorithm achieves a **(1 - 1/e) ~ 0.632** approximation ratio, which is optimal for this problem class.

### Sviridenko's Improvement (2004)

Sviridenko extended this to general monotone submodular functions under knapsack constraints:

```
Key insight: By enumerating all 3-element subsets as "seeds"
and running greedy from each, we achieve (1 - 1/e) approximation
for ANY monotone submodular function under budget constraint.

Cost: O(n^5) function evaluations (impractical for large n)
      Can be reduced to O(n^4) with thresholding techniques
```

---

## Value of Information (VOI)

### Expected Value of Perfect Information (EVPI)

EVPI measures the maximum value of eliminating all uncertainty before making a decision:

```
EVPI = E[max_a U(a, theta)] - max_a E[U(a, theta)]
         theta                        theta

"Expected utility knowing theta" - "Best utility without knowing theta"
```

In code search context:
- theta represents the true answer locations
- a represents which files to include in context
- U(a, theta) is how well the agent answers given context a and truth theta

### Expected Value of Sample Information (EVSI)

EVSI measures the value of partial information (more realistic):

```
EVSI = E[max_a E[U(a, theta)|X]] - max_a E[U(a, theta)]
        X      theta                      theta

"Expected utility after observing X" - "Current best utility"
```

### Properties

```
Key relationships:
------------------------------------------------------
0 <= EVSI <= EVPI

EVSI = 0 when additional information is useless
EVSI = EVPI when the sample provides perfect information
------------------------------------------------------
```

### Application to Active Context Selection

When selecting the next item to add to context:

```
VOI(c_i | S) = Expected improvement in answer quality
               by adding c_i to current selection S

             ~ P(c_i relevant | Q, S) * Information_Gain(c_i | S)
               - Cost(c_i) / Remaining_Budget
```

This leads to adaptive selection strategies that balance:
1. Probability the item is useful
2. How much new information it provides
3. Its cost relative to remaining budget

---

## Utility Functions for Code Search

### Decomposed Utility Model

For code search, utility should capture multiple dimensions:

```
U(S) = w_rel * Relevance(S, Q)
     + w_cov * Coverage(S, Q)
     + w_div * Diversity(S)
     + w_graph * GraphCoverage(S)
     + w_bridge * BridgingValue(S)
     - w_red * Redundancy(S)
```

### Component Functions

#### 1. Relevance (Query Match)

```sql
Relevance(S, Q) = SUM sim(s, Q) for s in S

-- SQL implementation
SELECT SUM(1 - cosine_distance(embedding, query_embedding))
FROM selected_items
```

#### 2. Coverage (Topic Completeness)

```
Coverage(S, Q) = |Topics(Q) intersect UNION Topics(s)|
                 ---------------------------------------
                           |Topics(Q)|

Topics can be:
  - Named entities (classes, functions)
  - Semantic concepts (from topic model)
  - Query terms (lexical coverage)
```

#### 3. Diversity (Semantic Spread)

```
Diversity(S) = det(K_S)  [DPP-based]

Or simplified:
Diversity(S) = SUM SUM (1 - sim(s_i, s_j)) for i < j
               normalized by |S|^2
```

#### 4. Graph Coverage

```
GraphCoverage(S) = |Reachable(S, k-hops) intersect Relevant(Q)|
                   --------------------------------------------
                              |Relevant(Q)|

"What fraction of relevant code is within k-hops of
 something we selected?"
```

#### 5. Bridging Value

```
BridgingValue(S) = SUM Betweenness(s) for s in S
                   where s connects different clusters

"Prefer items that connect different parts of the codebase"
```

### Submodularity of Components

| Component | Submodular? | Notes |
|-----------|-------------|-------|
| Relevance (sum) | Modular (linear) | Trivially submodular |
| Coverage | Submodular | Set coverage is classic example |
| Diversity (avg pairwise) | Not submodular | Use max or DPP instead |
| Diversity (DPP) | Log-submodular | Exact sampling efficient |
| Graph Coverage | Submodular | Reachability is coverage |
| Bridging | Approximately | Betweenness has diminishing returns |

### Ensuring Submodularity

To maintain approximation guarantees, ensure U(S) is submodular:

```
Strategy 1: Use only submodular components
Strategy 2: Use non-negative weighted sum of submodular functions
           (sum of submodular is submodular)
Strategy 3: Use concave composition
           (g(f(S)) is submodular if f is submodular and g is concave)
```

---

## Greedy Algorithms and Approximation Guarantees

### The Standard Greedy Algorithm

```
Algorithm: Greedy_Submodular(f, C, k)
============================================
Input:  f = submodular function, C = candidates, k = count
Output: S = selected set

S <- empty set
for i = 1 to k:
    gains <- {f(S union {c}) - f(S) : c in C \ S}
    c* <- argmax(gains)
    S <- S union {c*}
return S
```

### Guarantees Summary

| Constraint | Algorithm | Guarantee | Reference |
|-----------|-----------|-----------|-----------|
| Cardinality |S| <= k | Greedy | 1 - 1/e ~ 0.632 | Nemhauser et al. 1978 |
| Knapsack SUM c_i <= B | Modified Greedy | 1 - 1/e | Sviridenko 2004 |
| Matroid | Greedy | 1/2 | Fisher et al. 1978 |
| Matroid | Continuous Greedy | 1 - 1/e | Calinescu et al. 2011 |
| Multiple Knapsacks | Greedy + Enum | (1 - 1/e - eps) | Kulik et al. 2013 |

### Cost-Effective Greedy (for Budgets)

When items have variable costs, use **marginal gain per unit cost**:

```
Algorithm: Cost_Effective_Greedy(f, C, c, B)
============================================
Input:  f = utility, C = candidates, c = costs, B = budget
Output: S = selected set

S <- empty set
remaining <- B

while remaining > 0:
    feasible <- {x in C \ S : c[x] <= remaining}
    if feasible = empty: break

    for x in feasible:
        efficiency[x] <- (f(S union {x}) - f(S)) / c[x]

    x* <- argmax(efficiency)
    S <- S union {x*}
    remaining <- remaining - c[x*]

# Compare with best single item
for x in C:
    if c[x] <= B and f({x}) > f(S):
        S <- {x}

return S
```

### Why the Single-Item Check?

```
Counterexample where greedy fails without this check:

Items:  A (cost=1, value=2), B (cost=100, value=100)
Budget: 100

Greedy by efficiency picks A first (efficiency = 2/1 = 2)
Then budget = 99, can't fit B
Result: {A} with value 2

With single-item check:
B alone has value 100 > 2
Result: {B} with value 100

The check ensures we don't miss "high-value expensive items"
```

### Accelerated Greedy (Lazy Evaluation)

For large candidate sets, use lazy evaluation exploiting submodularity:

```
Algorithm: Lazy_Greedy(f, C, k)
============================================
Insight: Marginal gains can only decrease as S grows

S <- empty set
priority_queue <- [(f({c}), c, 0) for c in C]  # (gain, item, iteration)
current_iter <- 0

while |S| < k:
    current_iter += 1
    (gain, c, iter) <- priority_queue.pop_max()

    if iter == current_iter:
        # Gain is current, use this item
        S <- S union {c}
    else:
        # Recompute gain and reinsert
        new_gain <- f(S union {c}) - f(S)
        priority_queue.push((new_gain, c, current_iter))

return S
```

This often achieves **order of magnitude speedup** in practice.

---

## Implementation Strategies for DuckDB/SQL

### Schema for Budgeted Selection

```sql
-- Candidate items with embeddings and costs
CREATE TABLE candidates (
    id INTEGER PRIMARY KEY,
    uri TEXT NOT NULL,
    embedding FLOAT[384],       -- E5-small dimensions
    token_count INTEGER,        -- Cost
    relevance_score FLOAT,      -- Pre-computed query relevance
    cluster_id INTEGER,         -- For diversity
    graph_centrality FLOAT      -- Betweenness or PageRank
);

-- Pairwise similarities (materialized for efficiency)
CREATE TABLE candidate_similarities (
    id_a INTEGER,
    id_b INTEGER,
    similarity FLOAT,
    PRIMARY KEY (id_a, id_b)
);
```

### MMR Selection in SQL

```sql
-- Iterative MMR using a recursive CTE
WITH RECURSIVE mmr_selection AS (
    -- Base case: select highest relevance item
    SELECT
        id,
        uri,
        relevance_score AS mmr_score,
        token_count,
        1 AS iteration,
        ARRAY[id] AS selected_ids,
        token_count AS total_tokens
    FROM candidates
    ORDER BY relevance_score DESC
    LIMIT 1

    UNION ALL

    -- Recursive case: add item with best MMR score
    SELECT
        c.id,
        c.uri,
        (SELECT
            0.7 * c.relevance_score
            - 0.3 * MAX(cs.similarity)
         FROM candidate_similarities cs
         WHERE cs.id_a = c.id
           AND cs.id_b = ANY(m.selected_ids)
        ) AS mmr_score,
        c.token_count,
        m.iteration + 1,
        m.selected_ids || c.id,
        m.total_tokens + c.token_count
    FROM mmr_selection m, candidates c
    WHERE NOT c.id = ANY(m.selected_ids)
      AND m.total_tokens + c.token_count <= 4000  -- Budget
      AND m.iteration < 20  -- Max items
    ORDER BY mmr_score DESC
    LIMIT 1
)
SELECT * FROM mmr_selection;
```

### Coverage-Based Selection

```sql
-- Select items maximizing concept coverage under budget
WITH concept_coverage AS (
    SELECT
        c.id,
        c.token_count,
        c.relevance_score,
        ARRAY_AGG(DISTINCT cc.concept_id) AS concepts
    FROM candidates c
    JOIN candidate_concepts cc ON c.id = cc.candidate_id
    GROUP BY c.id, c.token_count, c.relevance_score
),
greedy_selection AS (
    -- Iterative greedy by marginal coverage / cost
    -- (Implementation requires procedural extension or UDF)
    SELECT
        id,
        token_count,
        concepts,
        relevance_score / token_count AS efficiency
    FROM concept_coverage
    ORDER BY efficiency DESC
)
SELECT * FROM greedy_selection
WHERE SUM(token_count) OVER (ORDER BY efficiency DESC) <= 4000;
```

### UDF for Submodular Maximization

```csharp
// C# UDF registration for DuckDB
[DuckDbFunction("greedy_select")]
public static object GreedySubmodularSelect(
    object[] candidateIds,
    object[] candidateCosts,
    object[] candidateEmbeddings,
    double budget,
    double lambda)
{
    var selected = new List<int>();
    var remaining = budget;
    var embeddings = candidateEmbeddings.Cast<float[]>().ToList();

    while (remaining > 0)
    {
        var bestId = -1;
        var bestScore = double.MinValue;

        for (int i = 0; i < candidateIds.Length; i++)
        {
            if (selected.Contains(i)) continue;
            var cost = (double)candidateCosts[i];
            if (cost > remaining) continue;

            // Compute MMR-style score
            var relevance = ComputeRelevance(embeddings[i]);
            var redundancy = selected.Count == 0 ? 0 :
                selected.Max(j => CosineSimilarity(embeddings[i], embeddings[j]));

            var score = (lambda * relevance - (1-lambda) * redundancy) / cost;

            if (score > bestScore)
            {
                bestScore = score;
                bestId = i;
            }
        }

        if (bestId < 0) break;
        selected.Add(bestId);
        remaining -= (double)candidateCosts[bestId];
    }

    return selected.Select(i => candidateIds[i]).ToArray();
}
```

### Macro for Token-Budgeted Search

```sql
-- Macro wrapping budgeted selection
CREATE MACRO budgeted_search(query_text, token_budget, lambda) AS (
    WITH query_embedding AS (
        SELECT embed(query_text) AS qe
    ),
    scored_candidates AS (
        SELECT
            n.uri,
            n.id,
            a.token_count,
            1 - array_cosine_distance(
                (SELECT qe FROM query_embedding),
                de.embedding
            ) AS relevance
        FROM node n
        JOIN document_embedding de ON n.artifact_id = de.artifact_id
        JOIN artifact a ON n.artifact_id = a.id
        WHERE n.scope = 'object'
    )
    SELECT * FROM greedy_select(
        ARRAY_AGG(id),
        ARRAY_AGG(token_count),
        ARRAY_AGG(embedding),
        token_budget,
        lambda
    )
);
```

---

## Practical Algorithms for Agent Context Selection

### Two-Phase Selection Pipeline

```
Phase 1: Candidate Generation (Recall-focused)
================================================
+-------------+     +-------------+     +-------------+
|   Query     |---->|  Retrieval  |---->|  Candidates |
|             |     |  (Top-100)  |     |  (Diverse)  |
+-------------+     +-------------+     +-------------+

Methods:
- Semantic search (embeddings)
- Lexical search (BM25)
- Graph expansion (2-hop neighbors)
- Reciprocal Rank Fusion to combine


Phase 2: Budgeted Selection (Precision-focused)
================================================
+-------------+     +-------------+     +-------------+
|  Candidates |---->|  Submodular |---->|   Context   |
|   (100)     |     |   Greedy    |     |  (Budget)   |
+-------------+     +-------------+     +-------------+

Methods:
- MMR with cost-awareness
- Coverage-based selection
- Graph-aware bridging bonus
```

### Adaptive Budget Allocation

```
Algorithm: Adaptive_Context_Selection
============================================
Input:  Q = query, B = total budget
Output: Context items with budget allocation

# 1. Analyze query complexity
complexity <- estimate_query_complexity(Q)
# Returns: simple, moderate, complex, multi-part

# 2. Allocate budget across phases
if complexity = simple:
    retrieval_budget <- 0.8 * B    # Most goes to top results
    expansion_budget <- 0.1 * B
    bridging_budget  <- 0.1 * B
elif complexity = complex:
    retrieval_budget <- 0.5 * B    # Need more diverse coverage
    expansion_budget <- 0.25 * B
    bridging_budget  <- 0.25 * B
elif complexity = multi-part:
    # Decompose query, allocate per part
    parts <- decompose_query(Q)
    per_part_budget <- B / len(parts)
    return [Adaptive_Context_Selection(p, per_part_budget) for p in parts]

# 3. Execute selection with allocated budgets
retrieval_results <- semantic_search(Q, k=50)
selected <- cost_effective_greedy(
    candidates=retrieval_results,
    budget=retrieval_budget,
    utility=relevance + diversity
)

expansion_candidates <- graph_expand(selected, hops=2)
selected += cost_effective_greedy(
    candidates=expansion_candidates,
    budget=expansion_budget,
    utility=coverage + bridging
)

return selected
```

### Real-Time MMR with Streaming

For interactive agents, compute selection incrementally:

```
Algorithm: Streaming_MMR_Selection
============================================
Input:  Q = query, stream of candidates, B = budget

selected <- empty set
used_budget <- 0
similarity_cache <- empty_dict

for each candidate c in stream:
    if cost(c) > B - used_budget:
        continue  # Can't afford

    # Compute relevance (can be precomputed)
    rel <- similarity(c.embedding, Q.embedding)

    # Compute max redundancy with selected items
    red <- 0
    for s in selected:
        key <- (c.id, s.id)
        if key not in similarity_cache:
            similarity_cache[key] <- similarity(c.embedding, s.embedding)
        red <- max(red, similarity_cache[key])

    mmr <- lambda * rel - (1-lambda) * red

    # Accept if positive MMR and improves coverage
    if mmr > threshold:
        selected.add(c)
        used_budget += cost(c)

        # Stop if budget exhausted
        if used_budget >= 0.95 * B:
            break

return selected
```

### DPP-Based Diverse Selection

For maximum diversity with relevance, use Determinantal Point Processes:

```
DPP Selection intuition:
------------------------------------------------------
Items are represented by feature vectors v_i.
Probability of selecting set S is proportional to:

P(S) ~ det(L_S)

where L_S is the submatrix of kernel L indexed by S.

L_ij = q_i * similarity(v_i, v_j) * q_j

q_i = "quality" of item i (relevance to query)
similarity = embedding similarity

Higher determinant = more diverse (vectors more orthogonal)
Higher quality = more relevant items more likely
------------------------------------------------------
```

```python
# k-DPP sampling for diverse retrieval
import numpy as np
from dppy.finite_dpps import FiniteDPP

def dpp_select(candidates, embeddings, relevances, k, budget):
    """Select k diverse, relevant items under budget."""

    # Build kernel matrix
    # L_ij = relevance_i * similarity_ij * relevance_j
    n = len(candidates)
    L = np.zeros((n, n))
    for i in range(n):
        for j in range(n):
            sim = np.dot(embeddings[i], embeddings[j])
            L[i,j] = relevances[i] * sim * relevances[j]

    # Sample from k-DPP
    dpp = FiniteDPP('likelihood', **{'L': L})

    # Rejection sampling for budget constraint
    for _ in range(100):  # Max attempts
        sample = dpp.sample_exact_k_dpp(k)
        total_cost = sum(candidates[i].cost for i in sample)
        if total_cost <= budget:
            return [candidates[i] for i in sample]

    # Fallback to greedy if DPP fails budget constraint
    return greedy_select(candidates, relevances, budget)
```

---

## Best Practices and Common Pitfalls

### Best Practices

#### 1. Always Consider Variable Costs

```
BAD:  Select top-5 by relevance
GOOD: Select items maximizing utility under token budget

Why: A highly relevant 2000-token file might be less valuable
     than four 400-token files covering different aspects.
```

#### 2. Tune Lambda for Use Case

```
Debugging specific bug:    lambda = 0.8-0.9 (precision matters)
Understanding architecture: lambda = 0.4-0.6 (coverage matters)
Exploring unfamiliar code:  lambda = 0.3-0.5 (diversity matters)

Can also adapt lambda based on:
- Query specificity (specific -> high lambda)
- Number of candidates (few -> high lambda, many -> lower lambda)
- Remaining budget (low -> increase lambda to focus)
```

#### 3. Pre-compute Pairwise Similarities

```sql
-- Materialize similarities for frequent candidate sets
CREATE TABLE cached_similarities AS
SELECT
    a.id AS id_a,
    b.id AS id_b,
    1 - array_cosine_distance(a.embedding, b.embedding) AS similarity
FROM frequently_retrieved a
CROSS JOIN frequently_retrieved b
WHERE a.id < b.id;

-- Use index for fast lookup
CREATE INDEX idx_sim_lookup ON cached_similarities(id_a, id_b);
```

#### 4. Use Lazy Evaluation for Large Candidate Sets

```
Naive greedy:  O(n * k * f_eval)  -- recompute all gains each iteration
Lazy greedy:   O(n * f_eval + k * log(n) * f_eval)  -- often much less

For n=1000, k=20, f_eval=1ms:
Naive: ~20,000 evaluations = 20 seconds
Lazy:  ~1,500 evaluations = 1.5 seconds (typical)
```

#### 5. Combine Multiple Utility Signals

```sql
-- Multi-objective utility function
utility(s) = w1 * relevance(s)           -- Query match
           + w2 * coverage_gain(s)        -- New concepts covered
           + w3 * graph_proximity(s)      -- Close to already-selected
           + w4 * recency(s)              -- Recent changes
           - w5 * redundancy(s)           -- Overlap with selected

-- Start with equal weights, tune based on feedback
```

### Common Pitfalls

#### 1. Ignoring Redundancy in Top-K

```
Pitfall: "Just take top-10 by embedding similarity"

Problem: Top results often describe the same thing differently
         (e.g., interface + implementation + tests of same class)

Solution: MMR or submodular selection to ensure diversity
```

#### 2. Fixed Token Budget Regardless of Query

```
Pitfall: "Always use 4000 tokens of context"

Problem: Simple queries need less; complex queries need more
         Wasting tokens on simple queries increases latency/cost

Solution: Adaptive budget based on query complexity
- Keyword lookup: 500-1000 tokens
- Understanding flow: 2000-3000 tokens
- Architecture question: 4000-6000 tokens
```

#### 3. Not Accounting for Cost in Optimization

```
Pitfall: Using MMR/submodular optimization without cost-weighting

Problem: May select many tiny irrelevant items over fewer
         substantial relevant items

Solution: Always use efficiency (gain/cost) not just gain
```

#### 4. Over-diversifying When Precision Needed

```
Pitfall: lambda = 0.3 for all queries

Problem: When user asks "show me the AuthService class",
         diversity is counterproductive

Solution: Detect query intent
- Specific entity -> high lambda (precision)
- Conceptual question -> balanced lambda
- Exploration -> low lambda (diversity)
```

#### 5. Ignoring Graph Structure

```
Pitfall: Selecting items purely by embedding similarity

Problem: Misses structural relationships
         - Implementation without interface
         - Function without callers
         - Config without code that reads it

Solution: Include graph-based utility components
- Bridging bonus for items connecting clusters
- Reachability coverage for ensuring completeness
```

### Performance Optimization Checklist

```
[ ] Pre-compute embeddings at index time
[ ] Materialize pairwise similarities for hot items
[ ] Use approximate nearest neighbor for candidate generation
[ ] Implement lazy greedy evaluation
[ ] Cache utility function subcomputations
[ ] Consider batched similarity computation (GPU)
[ ] Profile utility function - often the bottleneck
[ ] Set reasonable iteration limits (k_max, time_max)
```

---

## References

### Foundational Papers

1. **Carbonell, J., & Goldstein, J. (1998)**. "The Use of MMR, Diversity-Based Reranking for Reordering Documents and Producing Summaries." SIGIR 1998.
   - [PDF](https://www.cs.cmu.edu/~jgc/publication/The_Use_MMR_Diversity_Based_LTMIR_1998.pdf)
   - Introduced Maximal Marginal Relevance for balancing relevance and diversity

2. **Nemhauser, G. L., Wolsey, L. A., & Fisher, M. L. (1978)**. "An Analysis of Approximations for Maximizing Submodular Set Functions." Mathematical Programming, 14(1), 265-294.
   - [Springer Link](https://link.springer.com/article/10.1007/BF01588971)
   - Classic (1-1/e) approximation guarantee for greedy on submodular functions

3. **Lin, H., & Bilmes, J. (2011)**. "A Class of Submodular Functions for Document Summarization." ACL 2011.
   - [ACL Anthology](https://aclanthology.org/P11-1052/)
   - Submodular coverage + diversity functions for summarization

4. **Khuller, S., Moss, A., & Naor, J. (1999)**. "The Budgeted Maximum Coverage Problem." Information Processing Letters, 70(1), 39-45.
   - [ScienceDirect](https://www.sciencedirect.com/science/article/abs/pii/S0020019099000319)
   - (1-1/e) approximation for coverage under budget constraints

5. **Sviridenko, M. (2004)**. "A Note on Maximizing a Submodular Set Function Subject to a Knapsack Constraint." Operations Research Letters, 32(1), 41-43.
   - [PDF](https://thibaut.horel.org/submodularity/papers/sviridenko2004.pdf)
   - Extended budgeted coverage to general monotone submodular functions

### Value of Information

6. **Raiffa, H., & Schlaifer, R. (1961)**. "Applied Statistical Decision Theory." Harvard Business School.
   - Classic text introducing EVPI and EVSI concepts

7. **Howard, R. A. (1966)**. "Information Value Theory." IEEE Transactions on Systems Science and Cybernetics, 2(1), 22-26.
   - Foundational work on value of information in decision-making

### Active Feature Acquisition

8. **Attenberg, J., & Provost, F. (2011)**. "Selective Data Acquisition for Machine Learning."
   - [PDF](https://pages.stern.nyu.edu/~fprovost/Papers/selective_data_acq.pdf)
   - Comprehensive treatment of selective information acquisition

9. **Melville, P., et al. (2009)**. "Active Feature-Value Acquisition." Management Science, 55(4).
   - [ACM DL](https://dl.acm.org/doi/10.1287/mnsc.1080.0952)
   - Framework for cost-aware feature acquisition

### Diversity and DPPs

10. **Kulesza, A., & Taskar, B. (2012)**. "Determinantal Point Processes for Machine Learning." Foundations and Trends in Machine Learning, 5(2-3).
    - [arXiv](https://arxiv.org/abs/1207.6083)
    - Comprehensive tutorial on DPPs for diverse sampling

### RAG and Context Selection

11. **AdaGReS (2024)**. "Adaptive Greedy Context Selection via Redundancy-Aware Scoring for Token-Budgeted RAG."
    - [arXiv](https://arxiv.org/abs/2512.25052)
    - Recent work specifically on token-budgeted context selection for RAG

### Tutorials and Surveys

12. **Krause, A., & Golovin, D. (2014)**. "Submodular Function Maximization."
    - [Survey PDF](https://viterbi-web.usc.edu/~shanghua/teaching/Fall2023-670/krause12survey.pdf)
    - Comprehensive survey of submodular optimization

13. **Kun, J. (2014)**. "When Greedy Algorithms are Good Enough: Submodularity and the (1-1/e)-Approximation."
    - [Blog Post](https://www.jeremykun.com/2014/07/07/when-greedy-algorithms-are-good-enough-submodularity-and-the-1-1e-approximation/)
    - Accessible introduction to submodular greedy algorithms

### Graph Centrality

14. **Freeman, L. C. (1977)**. "A Set of Measures of Centrality Based on Betweenness." Sociometry, 40(1), 35-41.
    - [Wikipedia](https://en.wikipedia.org/wiki/Betweenness_centrality)
    - Foundational work on betweenness centrality for bridging nodes

### Implementation Resources

15. **DPPy Library**: Python library for sampling from DPPs
    - [Documentation](https://dppy.readthedocs.io/)

16. **Elasticsearch MMR**: Implementation of MMR for search diversification
    - [Elasticsearch Labs](https://www.elastic.co/search-labs/blog/maximum-marginal-relevance-diversify-results)
