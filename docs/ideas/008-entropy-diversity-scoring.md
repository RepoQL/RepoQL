# Entropy-Based Diversity Scoring

> Use information-theoretic measures to quantify and optimize result diversity

## Problem

MMR (idea 001) uses embedding similarity to measure redundancy. But similarity doesn't capture **information content**:

- Two files might be dissimilar in embedding space but cover the same concepts
- A highly informative outlier might be discarded as "redundant" to nothing

We need a principled measure of **information diversity**.

## Proposed Solution

Use **entropy** and **mutual information** from information theory to:
1. Measure how much information a result set covers
2. Select results that maximize information gain
3. Quantify query difficulty for adaptive search

```
┌─────────────────────────────────────────────────────────────────┐
│              Entropy-Based Selection                             │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Candidate Pool                                                 │
│   ┌─────────────────────────────────────────┐                   │
│   │ d1: AuthService (auth, jwt, token)      │                   │
│   │ d2: AuthMiddleware (auth, http, req)    │                   │
│   │ d3: JwtValidator (jwt, token, validate) │                   │
│   │ d4: UserService (user, crud, db)        │                   │
│   │ d5: ConfigLoader (config, env, load)    │                   │
│   └─────────────────────────────────────────┘                   │
│                                                                  │
│   Topic Distribution per doc:                                    │
│   d1: [0.6, 0.3, 0.1, 0.0]  (auth, token, other, user)         │
│   d2: [0.5, 0.1, 0.4, 0.0]                                      │
│   d3: [0.2, 0.7, 0.1, 0.0]                                      │
│   d4: [0.0, 0.0, 0.1, 0.9]                                      │
│   d5: [0.0, 0.0, 0.8, 0.2]                                      │
│                                                                  │
│   Selection by max entropy gain:                                 │
│   1. d1 (most relevant)                                         │
│   2. d4 (covers user topic, not auth)                           │
│   3. d3 (adds token depth beyond d1)                            │
│   ...                                                            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Key Formulas

### Shannon Entropy of Result Set

```
H(S) = -Σ p(t) * log₂(p(t))
       t∈topics

Where p(t) = normalized topic probability across selected docs
```

Higher entropy = more diverse topics covered.

### Information Gain from Adding Document

```
IG(d | S) = H(S ∪ {d}) - H(S)
```

Select document that maximizes information gain at each step.

### Mutual Information for Redundancy

```
I(d₁; d₂) = H(d₁) + H(d₂) - H(d₁, d₂)
```

High MI = redundant pair. Penalize adding documents with high MI to existing set.

## Implementation

### Step 1: Topic Representation

Use existing embeddings to derive topic distributions via soft clustering:

```sql
-- Derive topic probabilities from embeddings
-- Using k-means centroids as "topics"
CREATE TABLE doc_topics AS
WITH centroids AS (
    SELECT centroid_id, embedding as centroid
    FROM topic_centroids  -- precomputed k-means centers
),
distances AS (
    SELECT
        a.uri,
        c.centroid_id,
        1.0 / (1.0 + array_distance(a.embedding, c.centroid)) as similarity
    FROM artifact a
    CROSS JOIN centroids c
),
normalized AS (
    SELECT
        uri,
        centroid_id as topic,
        similarity / SUM(similarity) OVER (PARTITION BY uri) as prob
    FROM distances
)
SELECT uri, topic, prob
FROM normalized;
```

### Step 2: Entropy Calculation

```sql
-- Calculate entropy of a document set
CREATE MACRO set_entropy(uris) AS (
    WITH topic_probs AS (
        SELECT
            topic,
            SUM(prob) / array_length(uris) as p  -- average topic prob
        FROM doc_topics
        WHERE uri = ANY(uris)
        GROUP BY topic
    )
    SELECT -SUM(p * log2(p + 1e-10))  -- add epsilon for stability
    FROM topic_probs
    WHERE p > 0
);
```

### Step 3: Greedy Selection by Information Gain

```sql
-- Select documents maximizing entropy (information coverage)
CREATE MACRO select_diverse(candidates, k) AS (
    WITH RECURSIVE selection AS (
        -- Start with highest relevance doc
        SELECT
            ARRAY[(SELECT uri FROM candidates ORDER BY relevance DESC LIMIT 1)] as selected,
            1 as count

        UNION ALL

        -- Add doc with max information gain
        SELECT
            array_append(s.selected, best.uri),
            s.count + 1
        FROM selection s
        CROSS JOIN LATERAL (
            SELECT c.uri
            FROM candidates c
            WHERE c.uri != ALL(s.selected)
            ORDER BY (
                set_entropy(array_append(s.selected, c.uri))
                - set_entropy(s.selected)
            ) DESC
            LIMIT 1
        ) best
        WHERE s.count < k
    )
    SELECT unnest(selected) as uri
    FROM selection
    WHERE count = k
);
```

## Simpler Approach: Term Entropy

If topic modeling is too heavy, use term-based entropy:

```sql
-- Entropy based on term distribution
CREATE MACRO term_entropy(uri) AS (
    WITH term_counts AS (
        SELECT term, COUNT(*) as cnt
        FROM terms_in_doc
        WHERE doc_uri = uri
        GROUP BY term
    ),
    probs AS (
        SELECT term, cnt * 1.0 / SUM(cnt) OVER () as p
        FROM term_counts
    )
    SELECT -SUM(p * log2(p))
    FROM probs
);

-- Prefer high-entropy documents (more informative)
SELECT uri, relevance_score * (1 + 0.1 * term_entropy(uri)) as adjusted_score
FROM search_results
ORDER BY adjusted_score DESC;
```

## Query Difficulty Estimation

Information theory also helps estimate **query difficulty** (Cronen-Townsend et al.'s "clarity score"):

```sql
-- Clarity score: KL divergence between query model and collection model
CREATE MACRO query_clarity(query_terms) AS (
    WITH query_model AS (
        -- P(w|Q): term distribution in top retrieved docs
        SELECT term, SUM(tf) * 1.0 / SUM(SUM(tf)) OVER () as p_query
        FROM (
            SELECT term, tf FROM top_k_results  -- top 100 results for query
        )
        GROUP BY term
    ),
    collection_model AS (
        -- P(w|C): term distribution in entire collection
        SELECT term, SUM(tf) * 1.0 / SUM(SUM(tf)) OVER () as p_coll
        FROM all_docs_terms
        GROUP BY term
    )
    SELECT SUM(q.p_query * log2(q.p_query / c.p_coll))  -- KL divergence
    FROM query_model q
    JOIN collection_model c USING (term)
);

-- High clarity = focused query, easy to answer
-- Low clarity = ambiguous query, may need expansion/clarification
```

### Adaptive Search Based on Clarity

```sql
-- Adjust search behavior based on query difficulty
SELECT
    CASE
        WHEN query_clarity < 2.0 THEN 'expand'      -- ambiguous, expand query
        WHEN query_clarity > 5.0 THEN 'precise'     -- clear, top-k is fine
        ELSE 'normal'
    END as search_strategy
FROM (SELECT query_clarity('auth config'));
```

## Expected Benefits

| Application | Benefit |
|-------------|---------|
| Result diversity | Cover more topics in top-k |
| Adaptive expansion | Detect ambiguous queries early |
| Budget allocation | Spend tokens on high-entropy (informative) docs |
| Redundancy detection | Quantify overlap with MI |

## Comparison with MMR

| Aspect | MMR | Entropy-Based |
|--------|-----|---------------|
| Redundancy measure | Embedding similarity | Topic overlap / MI |
| Diversity goal | Geometric spread | Information coverage |
| Computation | O(k²) pairwise similarity | O(k) entropy updates |
| Interpretability | "Similar in embedding space" | "Covers same topics" |

**Recommendation**: Use both. MMR for fast geometric diversity, entropy for information-theoretic validation.

## Open Questions

1. How to choose number of topics for soft clustering?
2. Precompute topic distributions or on-the-fly?
3. Weight entropy vs relevance in final scoring?

## References

- [InformationTheory.md](../research/algorithms/InformationTheory.md) - Full theory
- Cronen-Townsend et al. (2002) - Predicting query performance (clarity score)
- Carbonell & Goldstein (1998) - MMR for comparison
- Lin & Bilmes (2011) - Submodular functions (entropy is submodular)
