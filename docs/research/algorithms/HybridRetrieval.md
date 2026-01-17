# Hybrid Retrieval Algorithms

Comprehensive documentation on hybrid retrieval systems that combine sparse (lexical) and dense (semantic) retrieval methods for optimal search performance.

## Table of Contents

1. [Overview](#overview)
2. [Sparse Retrieval (BM25 and BM25F)](#sparse-retrieval-bm25-and-bm25f)
3. [Dense Retrieval (Bi-Encoders and ANN)](#dense-retrieval-bi-encoders-and-ann)
4. [Learned Sparse Retrieval (SPLADE)](#learned-sparse-retrieval-splade)
5. [Late Interaction (ColBERT)](#late-interaction-colbert)
6. [Rank Fusion Methods](#rank-fusion-methods)
7. [DuckDB Implementation](#duckdb-implementation)
8. [Code-Specific Considerations](#code-specific-considerations)
9. [Benchmark Comparisons](#benchmark-comparisons)
10. [Best Practices and Pitfalls](#best-practices-and-pitfalls)
11. [References](#references)

---

## Overview

Hybrid retrieval combines multiple retrieval paradigms to leverage their complementary strengths. The most common approach fuses lexical (sparse) and semantic (dense) retrieval methods.

### Why Hybrid Retrieval?

```
+------------------+     +------------------+
|  Lexical Search  |     | Semantic Search  |
|     (BM25)       |     |   (Embeddings)   |
+--------+---------+     +--------+---------+
         |                        |
         | Exact matches          | Conceptual matches
         | Rare terms             | Synonyms/paraphrases
         | Domain jargon          | Intent understanding
         |                        |
         +----------+-------------+
                    |
            +-------v-------+
            | Rank Fusion   |
            | (RRF, Score)  |
            +-------+-------+
                    |
            +-------v-------+
            | Final Ranking |
            +---------------+
```

**Problem Statement**: Neither lexical nor semantic search is universally superior:

| Query Type | Lexical Wins | Semantic Wins |
|------------|--------------|---------------|
| Exact identifiers (`getUserById`) | Yes | No |
| Error codes (`ERR_404_NOT_FOUND`) | Yes | No |
| Conceptual queries ("how to authenticate") | No | Yes |
| Synonym matching ("delete" vs "remove") | No | Yes |
| Rare/technical terms | Yes | No |
| Typos and variations | No | Yes |

**Solution**: Combine both approaches through hybrid retrieval, typically achieving 15-20% improvement in nDCG@10 over either method alone.

### Retrieval Paradigms Compared

```
+------------------------------------------------------------------+
|                    RETRIEVAL ARCHITECTURE SPECTRUM                |
+------------------------------------------------------------------+
|                                                                   |
|  SPARSE            LEARNED SPARSE     LATE INTERACTION    DENSE  |
|  (BM25)            (SPLADE)           (ColBERT)          (Bi-enc)|
|                                                                   |
|  Exact matching    Learned term       Multi-vector       Single  |
|  Inverted index    expansion          per-token          vector  |
|  Fast, robust      Best of both       Expensive but      ANN     |
|                                        effective         index   |
|                                                                   |
|  <-- Efficiency                              Effectiveness -->   |
+------------------------------------------------------------------+
```

---

## Sparse Retrieval (BM25 and BM25F)

### BM25: The Probabilistic Relevance Framework

BM25 (Best Matching 25) is the most widely used lexical retrieval function, developed from the Probabilistic Relevance Framework by Robertson and Zaragoza.

#### BM25 Formula

```
                     (k1 + 1) * tf(t,d)
score(q,d) = SUM   ------------------------- * IDF(t)
             t in q  k1 * (1 - b + b * |d|/avgdl) + tf(t,d)

Where:
  tf(t,d)  = Term frequency of term t in document d
  |d|      = Document length (in terms)
  avgdl    = Average document length in collection
  k1       = Term saturation parameter (typically 1.2-2.0)
  b        = Length normalization parameter (typically 0.75)
  IDF(t)   = Inverse document frequency of term t
```

#### IDF Calculation

```
            N - n(t) + 0.5
IDF(t) = log ---------------
             n(t) + 0.5

Where:
  N     = Total number of documents
  n(t)  = Number of documents containing term t
```

#### Parameter Effects

| Parameter | Range | Effect |
|-----------|-------|--------|
| k1 | 1.2 - 2.0 | Higher = more weight to term frequency |
| b | 0.0 - 1.0 | Higher = more length normalization |
| k1 = 0 | - | Reduces to binary term presence |
| b = 0 | - | No length normalization |
| b = 1 | - | Full length normalization |

### BM25F: Field-Weighted Retrieval

BM25F extends BM25 for structured documents with multiple fields (title, body, anchor text, etc.).

#### BM25F Concept

```
+------------------------------------------+
|            DOCUMENT STRUCTURE            |
+------------------------------------------+
|  +----------+  +----------------------+  |
|  |  TITLE   |  |        BODY          |  |
|  | weight=3 |  |      weight=1        |  |
|  +----------+  +----------------------+  |
|  +------------------+                    |
|  |    IDENTIFIERS   |                    |
|  |    weight=5      |                    |
|  +------------------+                    |
+------------------------------------------+
```

#### BM25F Formula

```
                   (k1 + 1) * tf_weighted(t,d)
score(q,d) = SUM  --------------------------------- * IDF(t)
             t     k1 + tf_weighted(t,d)

Where:
  tf_weighted(t,d) = SUM  w_f * tf(t,d,f) / (1 + b_f * (|d_f|/avgdl_f - 1))
                     f

  w_f    = Field weight for field f
  b_f    = Length normalization for field f
  |d_f|  = Length of field f in document d
  avgdl_f = Average length of field f across collection
```

#### Field Weights for Code Search

| Field | Suggested Weight | Rationale |
|-------|------------------|-----------|
| Function name | 5.0 | Primary identifier |
| Class/type name | 4.0 | Major structural element |
| Parameter names | 3.0 | Important for API search |
| Comments/docs | 2.0 | Intent description |
| Body code | 1.0 | Baseline |

### BM25 Variants

| Variant | Key Difference | Use Case |
|---------|---------------|----------|
| BM25 | Original algorithm | General text retrieval |
| BM25F | Field weighting | Structured documents |
| BM25+ | Better lower-bound | Long documents |
| BM25L | Modified length norm | Variable-length documents |

---

## Dense Retrieval (Bi-Encoders and ANN)

### Bi-Encoder Architecture

Bi-encoders independently encode queries and documents into dense vector representations, enabling efficient similarity search.

```
+-------------+         +-------------+
|    Query    |         |  Document   |
+------+------+         +------+------+
       |                       |
+------v------+         +------v------+
|   Encoder   |         |   Encoder   |
|   (BERT)    |         |   (BERT)    |
+------+------+         +------+------+
       |                       |
+------v------+         +------v------+
| Query Vector|         |  Doc Vector |
|   [384-d]   |         |   [384-d]   |
+------+------+         +------+------+
       |                       |
       +----------+------------+
                  |
          +-------v-------+
          |    Cosine     |
          |  Similarity   |
          +---------------+
```

### Popular Bi-Encoder Models

| Model | Dimensions | BEIR Score | Notes |
|-------|------------|------------|-------|
| E5-small-v2 | 384 | 49.0 | Fast, good quality |
| E5-base-v2 | 768 | 50.3 | Balanced |
| E5-large-v2 | 1024 | 50.6 | Best E5 encoder-only |
| multilingual-e5-large | 1024 | 51.4 | 100 languages |
| E5-mistral-7b-instruct | 4096 | 56.9 | SOTA, resource-intensive |

### Approximate Nearest Neighbor (ANN) Indices

Dense retrieval requires efficient similarity search over millions of vectors. ANN indices trade exact accuracy for speed.

#### HNSW (Hierarchical Navigable Small Worlds)

```
Level 3:    *-------------------*     (few nodes, long-range)
            |                   |
Level 2:    *-----*-----*-------*     (more nodes)
            |     |     |       |
Level 1:    *-*-*-*-*-*-*-*-*-*-*     (most nodes, short-range)

Entry point at top level, greedy descent to find nearest neighbors
```

**HNSW Parameters**:

| Parameter | Typical Value | Effect |
|-----------|---------------|--------|
| M | 16-64 | Connections per node (memory vs recall) |
| efConstruction | 100-500 | Build-time quality (time vs recall) |
| efSearch | 50-200 | Query-time quality (latency vs recall) |

**HNSW Performance Characteristics**:

| Metric | Value | Conditions |
|--------|-------|------------|
| Recall@10 | >95% | Properly tuned |
| Query latency | 1-10ms | 1M vectors, GPU |
| Build time | O(n log n) | Incremental insertion |
| Memory | ~1.1x raw vectors | M=16 |

#### Other ANN Methods

| Method | Pros | Cons |
|--------|------|------|
| HNSW | Fast, high recall | Memory overhead |
| IVF-PQ | Compressed storage | Lower recall |
| ScaNN | Google-scale | Complex setup |
| Annoy | Simple, fast build | Lower recall |

---

## Learned Sparse Retrieval (SPLADE)

SPLADE (SParse Lexical AnD Expansion) learns sparse representations that combine the efficiency of inverted indices with the semantic understanding of neural models.

### SPLADE Architecture

```
+------------------+
|   Input Text     |
+--------+---------+
         |
+--------v---------+
|    BERT/MLM      |
|    Encoder       |
+--------+---------+
         |
+--------v---------+
|  Log-Saturation  |
|  + Sparsity Reg  |
+--------+---------+
         |
+--------v---------+
| Sparse Vector    |
| (vocabulary-dim) |
| Most entries = 0 |
+------------------+
```

### How SPLADE Works

1. **Token Importance**: MLM head predicts importance weights for all vocabulary tokens
2. **Log-Saturation**: `log(1 + ReLU(x))` prevents extreme weights
3. **Sparsity Regularization**: FLOPS regularization encourages sparse output
4. **Term Expansion**: Semantically related terms get non-zero weights

**Example SPLADE Expansion**:

```
Query: "machine learning tutorial"

Sparse representation (non-zero terms):
  machine:     0.82
  learning:    0.91
  tutorial:    0.75
  guide:       0.45    <-- expanded term
  course:      0.38    <-- expanded term
  ml:          0.52    <-- abbreviation expanded
  algorithm:   0.31    <-- semantic expansion
```

### SPLADE Variants

| Variant | Key Feature | Performance |
|---------|-------------|-------------|
| SPLADE | Original (SIGIR'21) | Baseline |
| SPLADE v2 | Distillation, doc-only expansion | +2-3% nDCG |
| SPLADE++ | Self-distillation | Better OOD |
| SPLADE v3 | 2024 improvements | Current SOTA |

### SPLADE vs BM25

| Aspect | BM25 | SPLADE |
|--------|------|--------|
| Term matching | Exact only | Exact + semantic |
| Index structure | Inverted index | Inverted index |
| Query latency | ~1ms | ~5-10ms |
| Index size | Small | 2-3x larger |
| Zero-shot performance | Good | Better |

---

## Late Interaction (ColBERT)

ColBERT (Contextualized Late Interaction over BERT) represents each document as multiple vectors (one per token), enabling fine-grained matching.

### ColBERT Architecture

```
Query: "find user authentication"

+--------+--------+--------+--------+
| [CLS]  |  find  |  user  |  auth  |
+--------+--------+--------+--------+
    |        |        |        |
    v        v        v        v
+--------+--------+--------+--------+
|  q_0   |  q_1   |  q_2   |  q_3   |  Query embeddings
+--------+--------+--------+--------+


Document: "getUserByToken validates the authentication token"

+--------+--------+--------+--------+--------+--------+
| getUser| ByToken| valid  | -ates  | auth   | token  |
+--------+--------+--------+--------+--------+--------+
    |        |        |        |        |        |
    v        v        v        v        v        v
+--------+--------+--------+--------+--------+--------+
|  d_0   |  d_1   |  d_2   |  d_3   |  d_4   |  d_5   |  Doc embeddings
+--------+--------+--------+--------+--------+--------+


MaxSim Operation:

For each q_i, find max similarity with any d_j:
  score = SUM_i( MAX_j( sim(q_i, d_j) ) )

  q_0 (CLS)  -> max with d_0: 0.6
  q_1 (find) -> max with d_2: 0.4
  q_2 (user) -> max with d_0: 0.9  <-- "getUser" matches "user"
  q_3 (auth) -> max with d_4: 0.95 <-- "auth" matches "auth"

  Total score: 0.6 + 0.4 + 0.9 + 0.95 = 2.85
```

### ColBERT Advantages

1. **Fine-grained matching**: Token-level interaction captures partial matches
2. **Offline document encoding**: Documents encoded once, queries at search time
3. **Interpretable**: Can identify which tokens matched

### ColBERTv2 Improvements

| Feature | ColBERT v1 | ColBERTv2 |
|---------|------------|-----------|
| Storage | ~150 bytes/token | ~25 bytes/token |
| Compression | None | Residual compression |
| Training | Pairwise | Denoised distillation |
| BEIR nDCG@10 | 49.8 | 52.0 |

### PLAID: Efficient ColBERT Engine

PLAID accelerates ColBERT through centroid-based pruning:

```
+------------------+
| Query Centroids  |
+--------+---------+
         |
+--------v---------+
| Centroid-based   |
| Candidate Filter |  <- Fast: bag-of-centroids scoring
+--------+---------+
         |
+--------v---------+
| Full MaxSim on   |
| Top Candidates   |  <- Accurate: full late interaction
+------------------+

Speedup: 7x (GPU), 45x (CPU)
```

---

## Rank Fusion Methods

### Reciprocal Rank Fusion (RRF)

RRF combines rankings from multiple retrieval systems using only rank positions, avoiding score normalization issues.

#### RRF Formula

```
           1
RRF(d) = SUM  -----------
          r   rank_r(d) + k

Where:
  r       = Each ranking system (e.g., BM25, dense)
  rank_r(d) = Position of document d in ranking r
  k       = Constant (typically 60)
```

#### RRF Example

```
BM25 Ranking:          Dense Ranking:
1. doc_A               1. doc_C
2. doc_B               2. doc_A
3. doc_C               3. doc_D
4. doc_D               4. doc_B

RRF Scores (k=60):
doc_A: 1/(1+60) + 1/(2+60) = 0.0164 + 0.0161 = 0.0325
doc_B: 1/(2+60) + 1/(4+60) = 0.0161 + 0.0156 = 0.0317
doc_C: 1/(3+60) + 1/(1+60) = 0.0159 + 0.0164 = 0.0323
doc_D: 1/(4+60) + 1/(3+60) = 0.0156 + 0.0159 = 0.0315

Final RRF Ranking: doc_A > doc_C > doc_B > doc_D
```

#### RRF Advantages

| Property | Benefit |
|----------|---------|
| Rank-based | No score normalization needed |
| Outlier-resistant | Extreme scores don't dominate |
| Simple | Easy to implement and tune |
| Effective | Consistently improves over single methods |

### Score Fusion Methods

#### Linear Combination

```
score(d) = alpha * score_sparse(d) + (1 - alpha) * score_dense(d)

Requires: Score normalization to comparable scales
```

#### Score Normalization Techniques

| Method | Formula | Properties |
|--------|---------|------------|
| Min-Max | (x - min) / (max - min) | Scales to [0,1], outlier-sensitive |
| L2 Norm | x / sqrt(sum(x^2)) | Unit length, preserves angles |
| Z-Score | (x - mean) / std | Handles different distributions |

#### Normalization Comparison

```
Raw Scores:           Min-Max:           L2 Norm:
BM25: [12, 8, 3]     [1.0, 0.56, 0.0]   [0.80, 0.53, 0.20]
Dense: [0.9, 0.8, 0.5] [1.0, 0.75, 0.0]   [0.69, 0.61, 0.38]

Issues:
- Min-Max sensitive to outliers
- L2 compresses range
- Both require knowing score distribution
```

### Fusion Method Comparison

| Method | Pros | Cons | When to Use |
|--------|------|------|-------------|
| RRF | Robust, no tuning | Ignores score magnitude | Default choice |
| Linear (Min-Max) | Uses score info | Outlier-sensitive | Known score distributions |
| Linear (L2) | Balanced | May compress important differences | Similar source quality |
| Weighted RRF | System-specific weights | Requires tuning | Unequal system quality |

### Empirical Findings

Recent research (2024) on rank fusion:

> "Contrary to existing studies, we found RRF to be sensitive to its parameters; that convex combination outperforms RRF in in-domain and out-of-domain settings."

**Recommendation**: Start with RRF (k=60), but evaluate linear combination with domain-specific tuning for production systems.

---

## DuckDB Implementation

DuckDB provides both full-text search (FTS) and vector similarity search (VSS) extensions, enabling hybrid retrieval in a single database.

### Setting Up Extensions

```sql
-- Install and load extensions
INSTALL fts;
INSTALL vss;
LOAD fts;
LOAD vss;
```

### Creating a Hybrid Search Schema

```sql
-- Documents table with text and embeddings
CREATE TABLE documents (
    id INTEGER PRIMARY KEY,
    title VARCHAR,
    content VARCHAR,
    embedding FLOAT[384]  -- E5-small dimensions
);

-- Create FTS index for BM25
PRAGMA create_fts_index(
    'documents',           -- table name
    'id',                  -- document ID column
    'title', 'content',    -- columns to index
    stemmer := 'porter',
    stopwords := 'english'
);

-- Create HNSW index for vector search
SET hnsw_enable_experimental_persistence = true;
CREATE INDEX documents_embedding_idx ON documents
USING HNSW (embedding)
WITH (metric = 'cosine');
```

### BM25 Search with DuckDB FTS

```sql
-- Basic BM25 search
SELECT
    id,
    title,
    match_bm25(id, 'authentication token') AS bm25_score
FROM documents
WHERE bm25_score IS NOT NULL
ORDER BY bm25_score DESC
LIMIT 10;

-- BM25 with field specification
SELECT
    id,
    title,
    match_bm25(
        id,
        'authentication',
        fields := 'title,content',
        k := 1.5,           -- term saturation
        b := 0.75           -- length normalization
    ) AS bm25_score
FROM documents
WHERE bm25_score IS NOT NULL
ORDER BY bm25_score DESC;

-- Conjunctive search (all terms required)
SELECT
    id,
    match_bm25(id, 'user authentication', conjunctive := 1) AS score
FROM documents
WHERE score IS NOT NULL;
```

### Vector Search with DuckDB VSS

```sql
-- Assume @query_embedding is the embedded query vector

-- Basic vector search
SELECT
    id,
    title,
    array_cosine_similarity(embedding, @query_embedding) AS similarity
FROM documents
ORDER BY similarity DESC
LIMIT 10;

-- Using HNSW index (faster for large datasets)
SELECT
    id,
    title,
    array_distance(embedding, @query_embedding) AS distance
FROM documents
ORDER BY distance
LIMIT 10;
```

### Implementing RRF in DuckDB

```sql
-- Hybrid search with RRF fusion
WITH bm25_results AS (
    SELECT
        id,
        ROW_NUMBER() OVER (ORDER BY match_bm25(id, @query) DESC) AS bm25_rank
    FROM documents
    WHERE match_bm25(id, @query) IS NOT NULL
    LIMIT 100
),
vector_results AS (
    SELECT
        id,
        ROW_NUMBER() OVER (
            ORDER BY array_cosine_similarity(embedding, @query_embedding) DESC
        ) AS vec_rank
    FROM documents
    LIMIT 100
),
combined AS (
    SELECT
        COALESCE(b.id, v.id) AS id,
        COALESCE(b.bm25_rank, 1000) AS bm25_rank,
        COALESCE(v.vec_rank, 1000) AS vec_rank
    FROM bm25_results b
    FULL OUTER JOIN vector_results v ON b.id = v.id
)
SELECT
    c.id,
    d.title,
    1.0 / (c.bm25_rank + 60) + 1.0 / (c.vec_rank + 60) AS rrf_score
FROM combined c
JOIN documents d ON c.id = d.id
ORDER BY rrf_score DESC
LIMIT 10;
```

### Weighted Score Fusion in DuckDB

```sql
-- Linear combination with min-max normalization
WITH bm25_scores AS (
    SELECT
        id,
        match_bm25(id, @query) AS raw_score
    FROM documents
    WHERE match_bm25(id, @query) IS NOT NULL
),
bm25_normalized AS (
    SELECT
        id,
        (raw_score - MIN(raw_score) OVER ()) /
        (MAX(raw_score) OVER () - MIN(raw_score) OVER () + 1e-10) AS norm_score
    FROM bm25_scores
),
vector_scores AS (
    SELECT
        id,
        array_cosine_similarity(embedding, @query_embedding) AS norm_score
    FROM documents
)
SELECT
    COALESCE(b.id, v.id) AS id,
    0.4 * COALESCE(b.norm_score, 0) +
    0.6 * COALESCE(v.norm_score, 0) AS hybrid_score
FROM bm25_normalized b
FULL OUTER JOIN vector_scores v ON b.id = v.id
ORDER BY hybrid_score DESC
LIMIT 10;
```

### RepoQL Integration Pattern

RepoQL's search macro combines these approaches:

```sql
-- RepoQL's search function signature
SELECT * FROM search(
    'authentication token refresh',  -- query
    k := 10,                         -- number of results
    scope := 'file:///src/%'         -- optional path filter
);

-- Internal implementation combines:
-- 1. BM25 on indexed text content
-- 2. Semantic similarity on document embeddings
-- 3. RRF fusion of results
```

### Performance Considerations

| Operation | Latency (1M docs) | Notes |
|-----------|------------------|-------|
| BM25 query | 5-20ms | Depends on term frequency |
| HNSW search | 1-5ms | After index warm-up |
| Full hybrid | 20-50ms | Including fusion |

**DuckDB-Specific Limitations**:

1. **FTS Index Updates**: Index doesn't auto-update; must rebuild on changes
2. **VSS Persistence**: HNSW experimental; WAL recovery not implemented
3. **Memory**: Both indices are memory-resident for performance

---

## Code-Specific Considerations

### Identifier Tokenization

Code search requires special handling of programming identifiers:

```
+-------------------------------------------+
|          IDENTIFIER TOKENIZATION          |
+-------------------------------------------+

CamelCase:  getUserById  ->  [get, User, By, Id]
snake_case: get_user_by_id -> [get, user, by, id]
kebab-case: get-user-by-id -> [get, user, by, id]
Mixed:      XMLHttpRequest -> [XML, Http, Request]

Special handling needed:
- Preserve original form for exact matching
- Generate subtokens for partial matching
- Handle abbreviations (XML, HTTP, ID)
```

### Code Search Challenges

| Challenge | Problem | Solution |
|-----------|---------|----------|
| Identifier splitting | `getUserById` vs "get user" | Subtoken indexing |
| Semantic gap | "delete" query, `remove` in code | Dense embeddings |
| Exact matching | Error codes, constants | BM25 with exact mode |
| Type awareness | Search for "String" methods | Field-weighted BM25F |

### Recommended Field Weights for Code

```sql
-- Example BM25F-style weighting for code search
CREATE TABLE code_index (
    id INTEGER,
    function_name VARCHAR,    -- weight: 5.0
    class_name VARCHAR,       -- weight: 4.0
    parameters VARCHAR,       -- weight: 3.0
    docstring VARCHAR,        -- weight: 2.0
    body_tokens VARCHAR       -- weight: 1.0
);

-- Simulated field weighting in query
SELECT
    id,
    5.0 * match_bm25(id, @query, fields := 'function_name') +
    4.0 * match_bm25(id, @query, fields := 'class_name') +
    3.0 * match_bm25(id, @query, fields := 'parameters') +
    2.0 * match_bm25(id, @query, fields := 'docstring') +
    1.0 * match_bm25(id, @query, fields := 'body_tokens') AS weighted_score
FROM code_index
ORDER BY weighted_score DESC;
```

### Code Embedding Models

| Model | MRR (CodeSearchNet) | Notes |
|-------|---------------------|-------|
| GraphCodeBERT | 0.509 | Data flow aware |
| CodeBERT | 0.117 | Basic code understanding |
| Voyage Code-3 | 0.973 | Commercial, SOTA |
| E5 (general) | ~0.4 | Not code-specific |

**Recommendation**: For code search, combine:
1. BM25 with identifier tokenization for exact matches
2. Code-specific embeddings (or fine-tuned E5) for semantic
3. RRF fusion with equal weights

### Hybrid Code Search Pipeline

```
+-----------------+
|   User Query    |
| "find user auth"|
+--------+--------+
         |
    +----v----+
    | Preproc |  <- Detect if identifier vs natural language
    +----+----+
         |
+--------+--------+
|                 |
v                 v
+--------+   +--------+
| BM25   |   | Dense  |
| Search |   | Search |
+---+----+   +----+---+
    |             |
    +------+------+
           |
    +------v------+
    | RRF Fusion  |
    +------+------+
           |
    +------v------+
    | Rerank with |
    | code model  |  <- Optional cross-encoder
    +-------------+
```

---

## Benchmark Comparisons

### BEIR Benchmark Results

BEIR (Benchmarking IR) evaluates zero-shot retrieval across 18 diverse datasets.

| Model | Type | BEIR Avg nDCG@10 | Notes |
|-------|------|------------------|-------|
| BM25 | Sparse | 43.4 | Strong baseline |
| DPR | Dense | 41.2 | Underperforms OOD |
| ANCE | Dense | 42.3 | Improved DPR |
| TAS-B | Dense | 44.4 | Knowledge distillation |
| E5-base-v2 | Dense | 50.3 | First to beat BM25 significantly |
| E5-large-v2 | Dense | 50.6 | Current strong dense |
| ColBERTv2 | Late-Int | 52.0 | Multi-vector |
| SPLADE++ | Learned Sparse | 50.8 | Sparse but neural |
| **Hybrid (BM25+E5)** | **Fusion** | **~52.6** | **Best practical choice** |

### Performance vs Efficiency Trade-offs

```
                    Effectiveness (nDCG@10)
                    40    45    50    55
                    |     |     |     |
BM25          [====|=====]           |  Fast, ~5ms
              |     |     |     |
DPR/ANCE      [====|====]            |  Medium, ~20ms
              |     |     |     |
E5-base       |     |     [====|===] |  Medium, ~25ms
              |     |     |     |
SPLADE        |     |     [====|==]  |  Slow, ~50ms
              |     |     |     |
ColBERTv2     |     |     |    [===| |  Slow, ~100ms
              |     |     |     |    |
Hybrid        |     |     |    [===|=]  Medium, ~30ms
              |     |     |     |
Query Latency: Fast-----------Medium-----------Slow
```

### Domain-Specific Performance

| Dataset Type | BM25 | Dense | Hybrid | Winner |
|--------------|------|-------|--------|--------|
| Web search | 0.45 | 0.52 | 0.55 | Hybrid |
| Scientific | 0.48 | 0.44 | 0.51 | Hybrid |
| Finance (FiQA) | 0.32 | 0.41 | 0.44 | Dense/Hybrid |
| COVID (Trec-Covid) | 0.60 | 0.65 | 0.71 | Hybrid |
| Code search | 0.38 | 0.42 | 0.47 | Hybrid |

**Key Insight**: Hybrid consistently wins or ties across domains, with largest gains on domain-shifted queries.

### Index Size Comparison

| Method | Index Size (1M docs, 512 avg tokens) |
|--------|--------------------------------------|
| BM25 (inverted index) | ~500 MB |
| Dense (384-d float32) | ~1.5 GB |
| Dense (384-d int8) | ~400 MB |
| SPLADE | ~1.2 GB |
| ColBERTv2 (compressed) | ~8 GB |

---

## Best Practices and Pitfalls

### Best Practices

#### 1. Start with Strong Baselines

```
DO:
- Implement BM25 first and measure
- Add dense retrieval incrementally
- Use RRF fusion initially
- Measure lift from hybrid

DON'T:
- Skip lexical baseline
- Over-engineer fusion
- Ignore evaluation metrics
```

#### 2. Tune for Your Domain

| Parameter | Default | Code Search | Document Search |
|-----------|---------|-------------|-----------------|
| BM25 k1 | 1.2 | 1.5 | 1.2 |
| BM25 b | 0.75 | 0.5 | 0.75 |
| RRF k | 60 | 60 | 60 |
| Fusion alpha | 0.5 | 0.4 (favor BM25) | 0.6 (favor dense) |

#### 3. Handle Edge Cases

```
Query Types Requiring Special Handling:

1. Exact identifiers: "getUserById"
   -> Prioritize BM25 exact match

2. Natural language: "how to authenticate users"
   -> Prioritize dense semantic search

3. Mixed: "implement OAuth authentication"
   -> Balanced hybrid fusion

4. Error codes: "ERR_AUTH_FAILED"
   -> BM25 only, no semantic needed
```

#### 4. Evaluation Strategy

```sql
-- Measure individual and hybrid performance
WITH ground_truth AS (...),
bm25_results AS (...),
dense_results AS (...),
hybrid_results AS (...)

SELECT
    'BM25' AS method,
    ndcg_at_k(bm25_results, ground_truth, 10) AS ndcg
UNION ALL
SELECT 'Dense', ndcg_at_k(dense_results, ground_truth, 10)
UNION ALL
SELECT 'Hybrid', ndcg_at_k(hybrid_results, ground_truth, 10);
```

### Common Pitfalls

#### 1. Score Distribution Mismatch

```
WRONG:
  hybrid_score = bm25_score + cosine_similarity
  (BM25: 0-50, Cosine: 0-1 -> BM25 dominates)

RIGHT:
  hybrid_score = normalize(bm25) + normalize(cosine)
  OR use RRF which ignores scores
```

#### 2. Index Staleness

```
PITFALL: FTS/HNSW indices not updated after document changes

SOLUTION:
- Rebuild FTS index periodically
- Use incremental HNSW updates where supported
- Track index freshness metadata
```

#### 3. Over-reliance on Benchmarks

```
BEIR =/= Your Domain

- BEIR tests zero-shot generalization
- Your use case may have:
  - Domain-specific vocabulary
  - User feedback available
  - Different query patterns

-> Always evaluate on domain-specific test sets
```

#### 4. Ignoring Query Analysis

```
Not all queries benefit from hybrid:

Short exact queries:    -> BM25 only (faster)
Long semantic queries:  -> Dense only (cheaper)
Mixed intent:           -> Full hybrid

Consider query classification to route efficiently
```

### Performance Optimization Tips

1. **Cache embeddings**: Document embeddings don't change; compute once
2. **Batch queries**: Group similar queries for vectorization
3. **Prune candidates**: Use BM25 top-1000 before dense reranking
4. **Quantize vectors**: INT8 gives 4x compression with <2% quality loss
5. **Shard indices**: Distribute HNSW across multiple cores

---

## References

### Foundational Papers

1. **BM25**: Robertson, S. and Zaragoza, H. (2009). [The Probabilistic Relevance Framework: BM25 and Beyond](https://www.staff.city.ac.uk/~sbrp622/papers/foundations_bm25_review.pdf). Foundations and Trends in Information Retrieval, 3(4), 333-389.

2. **SPLADE**: Formal, T., Piwowarski, B., and Clinchant, S. (2021). [SPLADE: Sparse Lexical and Expansion Model for First Stage Ranking](https://arxiv.org/abs/2107.05720). SIGIR'21.

3. **ColBERT**: Khattab, O. and Zaharia, M. (2020). [ColBERT: Efficient and Effective Passage Search via Contextualized Late Interaction over BERT](https://arxiv.org/abs/2004.12832). SIGIR'20.

4. **ColBERTv2**: Santhanam, K., Khattab, O., Saad-Falcon, J., Potts, C., and Zaharia, M. (2022). [ColBERTv2: Effective and Efficient Retrieval via Lightweight Late Interaction](https://arxiv.org/abs/2112.01488). NAACL'22.

5. **RRF**: Cormack, G.V., Clarke, C.L.A., and Buettcher, S. (2009). [Reciprocal Rank Fusion outperforms Condorcet and Individual Rank Learning Methods](https://dl.acm.org/doi/10.1145/1571941.1572114). SIGIR'09.

6. **BEIR**: Thakur, N., Reimers, N., Ruckle, A., Srivastava, A., and Gurevych, I. (2021). [BEIR: A Heterogeneous Benchmark for Zero-shot Evaluation of Information Retrieval Models](https://arxiv.org/abs/2104.08663). NeurIPS'21 Datasets Track.

7. **PLAID**: Santhanam, K., Khattab, O., Potts, C., and Zaharia, M. (2022). [PLAID: An Efficient Engine for Late Interaction Retrieval](https://arxiv.org/abs/2205.09707). CIKM'22.

8. **HNSW**: Malkov, Y.A. and Yashunin, D.A. (2016). [Efficient and Robust Approximate Nearest Neighbor Search Using Hierarchical Navigable Small World Graphs](https://arxiv.org/abs/1603.09320). IEEE TPAMI.

### DuckDB Resources

- [DuckDB Full-Text Search Extension](https://duckdb.org/docs/stable/core_extensions/full_text_search)
- [DuckDB Vector Similarity Search Extension](https://duckdb.org/docs/stable/core_extensions/vss)
- [Search in DuckDB: Integrating Full Text and Embedding Methods](https://motherduck.com/blog/search-using-duckdb-part-3/)
- [A Hybrid Information Retriever with DuckDB](https://aetperf.github.io/2024/05/30/A-Hybrid-information-retriever-with-DuckDB.html)

### Code Search

- [Microsoft CodeBERT](https://github.com/microsoft/CodeBERT)
- [Sourcegraph: Keeping it Boring with BM25F](https://sourcegraph.com/blog/keeping-it-boring-and-relevant-with-bm25f)
- [An Exploratory Study of Code Retrieval Techniques in Coding Agents](https://www.preprints.org/manuscript/202510.0924)

### Industry Implementations

- [OpenSearch Hybrid Search with RRF](https://opensearch.org/blog/introducing-reciprocal-rank-fusion-hybrid-search/)
- [Elasticsearch Linear Retriever for Hybrid Search](https://www.elastic.co/search-labs/blog/linear-retriever-hybrid-search)
- [Pinecone Hybrid Search Guide](https://docs.pinecone.io/guides/search/hybrid-search)
- [Weaviate Late Interaction Overview](https://weaviate.io/blog/late-interaction-overview)

### Sentence Transformers

- [Sentence Transformers Documentation](https://sbert.net/)
- [Retrieve and Re-Rank](https://www.sbert.net/examples/applications/information-retrieval/README.html)

---

*Document version: 1.0 | Last updated: January 2026*
