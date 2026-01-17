# Two-Stage Ranking: Rerankers and Learning-to-Rank

Comprehensive documentation on two-stage ranking architectures for code search, including cross-encoder reranking, learning-to-rank algorithms, and integration patterns for RepoQL.

## Table of Contents

1. [Overview](#overview)
2. [Why Two-Stage Ranking Matters for Code Search](#why-two-stage-ranking-matters-for-code-search)
3. [Architecture: Bi-Encoders vs Cross-Encoders](#architecture-bi-encoders-vs-cross-encoders)
4. [Cross-Encoder Reranking](#cross-encoder-reranking)
5. [Learning-to-Rank Fundamentals](#learning-to-rank-fundamentals)
6. [LambdaRank and LambdaMART](#lambdarank-and-lambdamart)
7. [Feature Engineering for Code Search](#feature-engineering-for-code-search)
8. [Available Reranker Models](#available-reranker-models)
9. [Integration Patterns](#integration-patterns)
10. [DuckDB Implementation Considerations](#duckdb-implementation-considerations)
11. [Latency vs Quality Tradeoffs](#latency-vs-quality-tradeoffs)
12. [Online Learning and Contextual Bandits](#online-learning-and-contextual-bandits)
13. [Best Practices and Common Pitfalls](#best-practices-and-common-pitfalls)
14. [References](#references)

---

## Overview

Two-stage ranking is an architectural pattern that separates retrieval into two distinct phases:

1. **Stage 1 (Retrieval)**: Fast, approximate candidate generation using vector search or lexical matching
2. **Stage 2 (Reranking)**: Precise, computationally intensive scoring of the candidate set

This approach enables systems to achieve both high recall (through broad initial retrieval) and high precision (through careful reranking) while maintaining acceptable latency.

```
+------------------+     +-------------------+     +------------------+
|                  |     |                   |     |                  |
|  Query Input     |---->|  Stage 1:         |---->|  Stage 2:        |
|                  |     |  Fast Retrieval   |     |  Cross-Encoder   |
|                  |     |  (100-1000 docs)  |     |  Reranking       |
+------------------+     +-------------------+     |  (top 50-100)    |
                                |                  +------------------+
                                |                          |
                         +------v------+            +------v------+
                         | Bi-Encoder  |            | Final Top-K |
                         | Embeddings  |            | Results     |
                         | or BM25     |            +-------------+
                         +-------------+
```

**Key Insight**: The two-stage architecture allows each component to be optimized for its specific role--retrieval for speed and recall, reranking for precision and semantic understanding.

---

## Why Two-Stage Ranking Matters for Code Search

Code search presents unique challenges that make two-stage ranking particularly valuable:

### The Scale Problem

Given a repository with 40 million code locations, running even a small reranking model (BERT-class) on a V100 GPU would take over 50 hours to return a single query result. Vector search with bi-encoders accomplishes the same task in under 100ms.

### The Precision Problem

Bi-encoders encode queries and documents independently, limiting their ability to capture nuanced query-document relationships. Cross-encoders process the query and document together, enabling much deeper semantic understanding--but this comes at significant computational cost.

### Code-Specific Challenges

| Challenge | Why It Matters | Two-Stage Solution |
|-----------|---------------|-------------------|
| Identifier matching | Exact names matter (e.g., `getUserById`) | BM25/sparse retrieval in Stage 1 |
| Semantic intent | "How does auth work?" requires understanding | Cross-encoder in Stage 2 |
| Multi-modal content | Code + comments + docs + configs | Hybrid retrieval + feature fusion |
| Graph relationships | Callers, callees, imports | Graph features in reranking |
| Recency and churn | Active code more relevant | Metadata features in LTR |

### Empirical Improvements

Practical evaluations consistently show significant improvements:

- **Hit Rate**: Improved from 0.854 to 0.895 (4.8% gain) using BGE-reranker-base
- **Mean Reciprocal Rank**: Improved from 0.640 to 0.708 (10.6% gain)
- **NDCG@10**: Cross-encoders achieve +28% improvement over baseline retrievers
- **Overall Quality**: 48% improvement in retrieval quality using hybrid three-stage pipelines (Pinecone benchmarks)

---

## Architecture: Bi-Encoders vs Cross-Encoders

Understanding the fundamental difference between these architectures is essential for designing two-stage systems.

### Bi-Encoder Architecture

```
Query: "user authentication"        Document: "def validate_token(...)..."
        |                                    |
        v                                    v
+----------------+                  +----------------+
|   Encoder      |                  |   Encoder      |
|   (BERT/E5)    |                  |   (BERT/E5)    |
+----------------+                  +----------------+
        |                                    |
        v                                    v
   [q1, q2, ..., qn]                   [d1, d2, ..., dn]
   Query Embedding                     Document Embedding
        |                                    |
        +-------------> cosine() <-----------+
                           |
                           v
                    Similarity Score
```

**Characteristics**:
- Processes query and document independently
- Embeddings can be pre-computed and cached
- Similarity via geometric distance (cosine, dot product)
- Fast: O(1) per document after indexing
- Less accurate for nuanced semantic matching

### Cross-Encoder Architecture

```
Query + Document: "[CLS] user authentication [SEP] def validate_token(...)... [SEP]"
                                    |
                                    v
                           +----------------+
                           |   Cross-Encoder|
                           |   (Full BERT)  |
                           |   Attention    |
                           +----------------+
                                    |
                                    v
                           +----------------+
                           | Classification |
                           | Head           |
                           +----------------+
                                    |
                                    v
                            Relevance Score
                              (0.0 - 1.0)
```

**Characteristics**:
- Processes query and document together
- Full cross-attention between all tokens
- No pre-computation possible
- Slow: O(n) where n = number of candidates
- More accurate for semantic understanding

### Comparison Table

| Aspect | Bi-Encoder | Cross-Encoder |
|--------|-----------|---------------|
| **Input** | Query and document separately | Query-document pair together |
| **Output** | Embedding vectors | Relevance score (0-1) |
| **Pre-computation** | Yes (cache document embeddings) | No (must compute per query) |
| **Speed** | Fast (~5ms per 1000 docs with index) | Slow (~12ms per single pair) |
| **Accuracy** | Good | Better (+10-30% on precision metrics) |
| **Scalability** | Excellent (ANN indices) | Poor (linear in candidates) |
| **Best Use** | Stage 1: Candidate retrieval | Stage 2: Reranking top candidates |
| **Cross-query consistency** | No (scores not comparable) | Yes (normalized 0-1 scores) |

### Hybrid Pipeline Rationale

The two-stage approach leverages the complementary strengths:

1. **Bi-encoder retrieves candidates**: Uses pre-computed embeddings + ANN index for sub-100ms retrieval of top-k candidates (typically 100-1000)
2. **Cross-encoder reranks**: Processes only the candidate set, achieving high precision where it matters most (top 10-20 results)

---

## Cross-Encoder Reranking

Cross-encoders are the workhorse of Stage 2 reranking. They process query-document pairs through full transformer attention, enabling deep semantic understanding.

### How Cross-Encoders Work

```python
# Conceptual cross-encoder scoring
def cross_encoder_score(query: str, document: str, model) -> float:
    # Concatenate query and document
    input_text = f"[CLS] {query} [SEP] {document} [SEP]"

    # Tokenize
    tokens = tokenizer(input_text, max_length=512, truncation=True)

    # Forward pass through transformer
    outputs = model(**tokens)

    # Classification head produces relevance score
    logits = outputs.logits  # Shape: [1, 2] for binary relevance
    score = softmax(logits)[1]  # Probability of "relevant"

    return score
```

### Cross-Encoder Benefits

1. **Full Attention**: Every query token attends to every document token
2. **Compositional Reasoning**: Can understand complex relationships ("find the function that calls X but not Y")
3. **Consistent Scores**: Outputs normalized 0-1 scores comparable across queries
4. **Threshold Setting**: Enables relevance cutoffs for RAG quality control

### Practical Considerations

**Input Limits**: Most cross-encoders have 512 token limits. For code search:
- Truncate long documents to first 512 tokens
- Or use sliding windows with max-pooling
- Or chunk documents and aggregate scores

**Batch Processing**: Cross-encoders benefit significantly from batching:

```python
# Efficient batched scoring
def batch_rerank(query: str, documents: list[str], model, batch_size=32):
    pairs = [(query, doc) for doc in documents]
    scores = []

    for i in range(0, len(pairs), batch_size):
        batch = pairs[i:i + batch_size]
        batch_scores = model.predict(batch)
        scores.extend(batch_scores)

    return scores
```

---

## Learning-to-Rank Fundamentals

Learning-to-Rank (LTR) is a machine learning approach to ranking that learns from labeled relevance data. Unlike cross-encoders (which are end-to-end neural models), LTR combines arbitrary features using gradient boosted trees or neural networks.

### LTR Problem Formulation

Given:
- A query q
- A set of candidate documents D = {d1, d2, ..., dn}
- Feature vectors x_i = f(q, d_i) for each query-document pair
- Relevance labels y_i (binary, graded, or pairwise preferences)

Learn a scoring function s(x) that ranks documents by predicted relevance.

### LTR Approaches

| Approach | Training Signal | Loss Function | Examples |
|----------|----------------|---------------|----------|
| **Pointwise** | Individual relevance labels | MSE, Cross-entropy | Regression, Classification |
| **Pairwise** | Document pair preferences | Hinge loss, Cross-entropy | RankNet, RankSVM |
| **Listwise** | Full ranking quality | NDCG, MAP approximations | LambdaRank, ListNet |

### Pairwise Approach (RankNet Foundation)

The key insight of pairwise LTR is that learning to order pairs correctly is sufficient for learning to rank:

```
For documents d_i and d_j where d_i > d_j (more relevant):

P(d_i > d_j) = sigmoid(s_i - s_j)

Loss = -log(P(d_i > d_j)) = log(1 + exp(-(s_i - s_j)))
```

This formulation allows gradient-based optimization of ranking quality.

### Why LTR Matters for Code Search

LTR enables combining heterogeneous signals that cross-encoders cannot easily capture:

- **Text relevance**: BM25 score, embedding similarity
- **Graph features**: PageRank, PPR distance, call graph depth
- **Metadata**: File recency, code churn, test coverage
- **User signals**: Click-through rates, dwell time (if available)

---

## LambdaRank and LambdaMART

LambdaRank and LambdaMART are among the most successful LTR algorithms, developed at Microsoft Research and used in production at Bing. They won the Yahoo! Learning to Rank Challenge in 2010.

### Evolution: RankNet -> LambdaRank -> LambdaMART

```
RankNet (2005)
    |
    | "Gradients don't need cost function"
    v
LambdaRank (2006)
    |
    | "Use gradient boosted trees"
    v
LambdaMART (2010)
```

### RankNet

RankNet introduced the pairwise cross-entropy loss for ranking:

```
Given: scores s_i, s_j for documents d_i, d_j
Target: P_ij = probability that d_i should rank higher than d_j

Loss: C = -P_ij * log(o_ij) - (1 - P_ij) * log(1 - o_ij)

where o_ij = sigmoid(s_i - s_j)
```

### LambdaRank

LambdaRank's key insight: **you only need gradients, not the cost function itself**.

The "lambda" gradients incorporate the change in NDCG from swapping document pairs:

```
lambda_ij = |delta_NDCG_ij| * gradient_cross_entropy

where delta_NDCG_ij = NDCG(after swap) - NDCG(before swap)
```

**Physical Intuition**: Think of documents as point masses. Lambda gradients are forces pushing relevant documents up and irrelevant documents down, with force magnitude proportional to the NDCG impact of the swap.

```
Query Results (before):
    1. doc_B (irrelevant)   <-- lambda pushes DOWN
    2. doc_A (relevant)     <-- lambda pushes UP
    3. doc_C (relevant)

The lambda between positions 1-2 is large because swapping
would significantly improve NDCG (relevant doc moves to top).
```

### LambdaMART

LambdaMART combines LambdaRank's gradient formulation with MART (Multiple Additive Regression Trees), also known as gradient boosted decision trees:

```
Final Score = sum of T trees:

s(x) = sum_{t=1}^{T} alpha_t * h_t(x)

where each tree h_t is trained to predict the lambda gradients
from the previous iteration's residuals.
```

**Key Advantages**:
- Handles heterogeneous features naturally (numeric, categorical, sparse)
- Fast inference (tree traversal)
- Interpretable feature importance
- Robust to outliers and missing values
- State-of-the-art performance on standard LTR benchmarks

### Implementation Options

| Library | Notes |
|---------|-------|
| **XGBoost** | Native `rank:ndcg` and `rank:pairwise` objectives |
| **LightGBM** | `lambdarank` objective, efficient for large datasets |
| **CatBoost** | `YetiRank` (similar to LambdaMART) |
| **TensorFlow Ranking** | Neural LTR with various losses |

Example with XGBoost:

```python
import xgboost as xgb

# Data format: features, labels, and query groups
dtrain = xgb.DMatrix(features, label=relevance_labels)
dtrain.set_group(query_group_sizes)

params = {
    'objective': 'rank:ndcg',
    'eval_metric': 'ndcg@10',
    'eta': 0.1,
    'max_depth': 6,
    'min_child_weight': 0.1,
    'lambda': 1.0,
}

model = xgb.train(params, dtrain, num_boost_round=100)
```

---

## Feature Engineering for Code Search

Effective LTR for code search requires domain-specific features. These can be grouped into several categories:

### Text Relevance Features

| Feature | Description | Computation |
|---------|-------------|-------------|
| BM25 Score | Lexical relevance | Standard BM25 formula |
| Embedding Similarity | Semantic relevance | Cosine similarity of bi-encoder embeddings |
| Query Term Coverage | Fraction of query terms found | Exact/fuzzy matching |
| Title/Symbol Match | Match in prominent positions | BM25F or weighted match |
| Code Comment Match | Match in documentation | Separate text field scoring |

### Graph Features

| Feature | Description | Computation |
|---------|-------------|-------------|
| Personalized PageRank | Proximity to seed nodes | PPR from query-related nodes |
| In-Degree | How many things reference this | Count incoming edges |
| Out-Degree | How many things this references | Count outgoing edges |
| Call Graph Distance | Hops from entry points | BFS from main/exports |
| Import Distance | Hops in dependency graph | Transitive dependency depth |
| Same Package | Query context shares package | Boolean or jaccard similarity |

### Metadata Features

| Feature | Description | Computation |
|---------|-------------|-------------|
| File Recency | When was file last modified | Days since last commit |
| Code Churn | How often does this code change | Lines changed / time period |
| File Age | How long has file existed | Days since creation |
| Test Coverage | Is this code tested | Coverage percentage |
| Documentation Quality | Has docstrings/comments | Comment ratio, doc presence |
| File Size | Lines of code | Normalized line count |
| Cyclomatic Complexity | Code complexity | Static analysis metric |

### Structural Features

| Feature | Description | Computation |
|---------|-------------|-------------|
| Symbol Type | Function, class, variable, etc. | From AST/semantic index |
| Visibility | Public, private, exported | Language-specific analysis |
| Parameter Count | Function arity | Count from signature |
| Nesting Depth | How deep in module hierarchy | Path component count |
| Language | Programming language | File extension/detection |

### Example Feature Vector

```
Query: "validate user authentication token"
Document: src/auth/token_validator.py::validate_jwt()

Feature Vector:
{
    // Text features
    "bm25_score": 12.4,
    "embedding_similarity": 0.82,
    "query_term_coverage": 0.75,
    "symbol_name_match": 0.6,       // "validate" matches

    // Graph features
    "pagerank": 0.0023,
    "in_degree": 15,                // Called by 15 functions
    "out_degree": 3,                // Calls 3 functions
    "call_distance_from_main": 2,

    // Metadata features
    "days_since_modified": 7,
    "churn_rate_30d": 0.15,         // 15% of lines changed
    "file_age_days": 365,
    "test_coverage": 0.85,

    // Structural features
    "symbol_type": "function",      // Encoded as one-hot
    "visibility": "public",
    "param_count": 2,
    "nesting_depth": 2,             // auth/token_validator
    "language": "python"
}
```

### Feature Engineering Best Practices

1. **Normalize features**: Use z-score or min-max normalization for numeric features
2. **Handle missing values**: Trees handle missing values well; use -1 or separate indicator
3. **Log-transform skewed features**: Apply log(1+x) to counts like in-degree
4. **Create interaction features**: BM25 * recency, similarity * pagerank
5. **Bucket continuous features**: Discretize for robustness (recency: <1d, <7d, <30d, >30d)

### Feature Injection for Cross-Encoders

Research shows that concatenating feature information as text can improve cross-encoder performance:

```
Query: "validate user authentication"
Document (with features): "validate_jwt() [BM25=12.4] [PageRank=High] [Churn=Low]
    def validate_jwt(token: str) -> bool:
        ..."
```

TREC 2023 Deep Learning Track found that appending BM25 scores as text tokens improves BERT-based reranker accuracy by 7.3% MRR@10.

---

## Available Reranker Models

### Open Source Cross-Encoders

#### BGE Reranker Family (BAAI)

The BGE rerankers from Beijing Academy of AI are popular open-source options:

| Model | Parameters | Languages | Notes |
|-------|-----------|-----------|-------|
| bge-reranker-base | 278M | EN/ZH | Good balance of speed/quality |
| bge-reranker-large | 560M | EN/ZH | Higher accuracy |
| bge-reranker-v2-m3 | 568M | 100+ | Multilingual, 8K context |
| bge-reranker-v2-gemma | 2.6B | EN | LLM-based, highest accuracy |
| bge-reranker-v2-minicpm-layerwise | 2.7B | EN/ZH | Configurable layer depth |

**Usage**:

```python
from FlagEmbedding import FlagReranker

reranker = FlagReranker('BAAI/bge-reranker-base', use_fp16=True)

query = "user authentication"
documents = ["def validate_token()...", "class User:...", ...]

scores = reranker.compute_score([[query, doc] for doc in documents])
```

#### MS MARCO Cross-Encoders

Pre-trained on MS MARCO passage ranking dataset:

| Model | Parameters | Notes |
|-------|-----------|-------|
| cross-encoder/ms-marco-MiniLM-L-6-v2 | 22M | Fast, good for latency-critical |
| cross-encoder/ms-marco-TinyBERT-L-2-v2 | 4.4M | Ultra-fast |
| cross-encoder/ms-marco-electra-base | 110M | Strong accuracy |

### Commercial APIs

#### Cohere Rerank

- **Rerank 3**: Best quality, 100+ languages, automatic chunking
- **Rerank 3 Nimble**: Optimized for production latency
- Normalized 0-1 scores, consistent across queries
- Max 1024 tokens per document, 256 tokens per query, 100 documents per request

```python
import cohere

co = cohere.Client('api-key')
results = co.rerank(
    query="user authentication",
    documents=["doc1...", "doc2...", ...],
    top_n=10,
    model='rerank-english-v3.0'
)
```

#### Voyage AI Rerank

- **rerank-1**: General purpose
- **rerank-lite-1**: Faster, lower cost
- Strong performance on code and technical content

#### Jina AI Rerank

- **jina-reranker-v1-base-en**: English-focused
- **jina-reranker-v1-turbo-en**: Faster variant
- Good latency/quality tradeoff

### Model Selection Guide

| Scenario | Recommended Model | Rationale |
|----------|------------------|-----------|
| Latency-critical (<50ms) | ms-marco-TinyBERT-L-2-v2 | Smallest, fastest |
| Balanced (50-200ms) | bge-reranker-base | Good accuracy, reasonable speed |
| Accuracy-critical | bge-reranker-v2-gemma | LLM-based, highest quality |
| Multilingual | bge-reranker-v2-m3 | 100+ languages |
| Production API | Cohere Rerank 3 Nimble | Managed, consistent |
| Self-hosted with GPU | bge-reranker-large | Open source, high quality |

---

## Integration Patterns

### Basic Retrieve-Then-Rerank Pipeline

```
                    Query
                      |
                      v
            +-------------------+
            | Bi-Encoder        |
            | Query Embedding   |
            +-------------------+
                      |
                      v
            +-------------------+
            | Vector Search     |
            | (HNSW Index)      |
            | top_k = 100       |
            +-------------------+
                      |
                      v
            +-------------------+
            | Cross-Encoder     |
            | Reranking         |
            | top_n = 10        |
            +-------------------+
                      |
                      v
                Final Results
```

### Hybrid Search + Reranking

```
                    Query
                      |
         +-----------+-----------+
         |                       |
         v                       v
+----------------+      +----------------+
| Sparse Search  |      | Dense Search   |
| (BM25)         |      | (Embeddings)   |
| top_k = 100    |      | top_k = 100    |
+----------------+      +----------------+
         |                       |
         +-----------+-----------+
                     |
                     v
            +-------------------+
            | Score Fusion      |
            | (RRF or Convex)   |
            | top_k = 100       |
            +-------------------+
                     |
                     v
            +-------------------+
            | Cross-Encoder     |
            | Reranking         |
            | top_n = 10        |
            +-------------------+
                     |
                     v
                Final Results
```

### Multi-Stage Pipeline (Maximum Quality)

```
                    Query
                      |
         +-----------+-----------+
         |                       |
         v                       v
+----------------+      +----------------+
| BM25 Retrieval |      | Dense Retrieval|
| top_k = 500    |      | top_k = 500    |
+----------------+      +----------------+
         |                       |
         +-----------+-----------+
                     |
                     v
            +-------------------+
            | RRF Fusion        |
            | Deduplicate       |
            | top_k = 200       |
            +-------------------+
                     |
                     v
            +-------------------+
            | LTR Model         |
            | (LambdaMART)      |
            | With features     |
            | top_k = 50        |
            +-------------------+
                     |
                     v
            +-------------------+
            | Cross-Encoder     |
            | Final rerank      |
            | top_n = 10        |
            +-------------------+
                     |
                     v
                Final Results
```

### Score Fusion Methods

#### Reciprocal Rank Fusion (RRF)

Simple, effective, and parameter-free (except k):

```
RRF_score(d) = sum over systems s of: 1 / (k + rank_s(d))

Typical k = 60
```

```sql
-- DuckDB RRF implementation
WITH sparse_results AS (
    SELECT uri, row_number() OVER (ORDER BY bm25_score DESC) as sparse_rank
    FROM fts_search('query', k := 100)
),
dense_results AS (
    SELECT uri, row_number() OVER (ORDER BY similarity DESC) as dense_rank
    FROM vector_search('query', k := 100)
)
SELECT
    COALESCE(s.uri, d.uri) as uri,
    1.0 / (60 + COALESCE(s.sparse_rank, 1000)) +
    1.0 / (60 + COALESCE(d.dense_rank, 1000)) as rrf_score
FROM sparse_results s
FULL OUTER JOIN dense_results d ON s.uri = d.uri
ORDER BY rrf_score DESC
LIMIT 100;
```

#### Convex Combination

Linear combination with tunable weight:

```
combined_score = alpha * normalize(sparse_score) + (1 - alpha) * normalize(dense_score)

Typical alpha = 0.3 to 0.7 (tune on validation set)
```

---

## DuckDB Implementation Considerations

RepoQL uses DuckDB as its query engine. Here are considerations for implementing two-stage ranking.

### Vector Search via VSS Extension

DuckDB's VSS extension provides HNSW indexing:

```sql
-- Load extension
INSTALL vss;
LOAD vss;

-- Create HNSW index
CREATE INDEX embeddings_idx ON document_embedding
USING HNSW (embedding)
WITH (metric = 'cosine');

-- Vector search
SELECT uri, array_cosine_similarity(embedding, query_embedding) as score
FROM document_embedding
ORDER BY embedding <-> query_embedding  -- Uses HNSW index
LIMIT 100;
```

**Current Limitations**:
- HNSW persistence is experimental (WAL recovery issues)
- For production, consider rebuilding index on startup or using in-memory mode

### Hybrid Search Implementation

```sql
-- Hybrid search with RRF fusion
WITH query_embedding AS (
    SELECT embed('query: user authentication') as vec
),
sparse AS (
    SELECT uri, match_bm25(content, 'user authentication') as score,
           row_number() OVER (ORDER BY score DESC) as rank
    FROM artifacts
    WHERE score > 0
    ORDER BY score DESC
    LIMIT 100
),
dense AS (
    SELECT de.uri, array_cosine_similarity(de.embedding, q.vec) as score,
           row_number() OVER (ORDER BY array_cosine_similarity(de.embedding, q.vec) DESC) as rank
    FROM document_embedding de, query_embedding q
    ORDER BY de.embedding <-> q.vec
    LIMIT 100
)
SELECT
    COALESCE(s.uri, d.uri) as uri,
    1.0 / (60 + COALESCE(s.rank, 1000)) +
    1.0 / (60 + COALESCE(d.rank, 1000)) as rrf_score
FROM sparse s
FULL OUTER JOIN dense d ON s.uri = d.uri
ORDER BY rrf_score DESC
LIMIT 100;
```

### Reranking via UDF

Cross-encoder reranking requires calling external models. Options:

1. **UDF calling Python model**:

```sql
-- Register UDF (in startup code)
CREATE FUNCTION rerank(query VARCHAR, docs VARCHAR[])
RETURNS FLOAT[]
LANGUAGE PYTHON
AS $$
from your_reranker import rerank_model
return rerank_model.score(query, docs)
$$;

-- Use in query
SELECT uri, score
FROM (
    SELECT uri, unnest(rerank('user auth', array_agg(content))) as score
    FROM candidates
)
ORDER BY score DESC
LIMIT 10;
```

2. **External reranking service** (recommended for latency control):

```csharp
// C# UDF implementation sketch
public class RerankerUdf
{
    private readonly CrossEncoderClient _client;

    public float[] Rerank(string query, string[] documents)
    {
        return _client.Score(query, documents);
    }
}
```

3. **Post-query reranking** (simplest):

```csharp
// Retrieve candidates via SQL, rerank in application code
var candidates = await dataStore.Query<SearchResult>(
    "SELECT uri, content FROM search(...) LIMIT 100");

var reranked = await reranker.RerankAsync(query, candidates);
return reranked.Take(10);
```

### Feature Extraction for LTR

DuckDB excels at feature engineering via SQL:

```sql
-- Extract LTR features for candidates
WITH candidates AS (
    SELECT uri FROM search('query', k := 100)
),
features AS (
    SELECT
        c.uri,
        -- Text features
        match_bm25(a.content, 'query') as bm25_score,
        array_cosine_similarity(de.embedding, query_embedding) as embed_sim,

        -- Graph features
        (SELECT count(*) FROM edge WHERE target_uri = c.uri) as in_degree,
        (SELECT count(*) FROM edge WHERE source_uri = c.uri) as out_degree,
        n.pagerank,

        -- Metadata features
        a.modified_at,
        epoch(now()) - epoch(a.modified_at) as age_seconds,
        n.lines,

        -- Structural features
        n.scope as symbol_type,
        a.lang

    FROM candidates c
    JOIN artifact a ON c.uri = a.uri
    JOIN node n ON c.uri = n.uri
    LEFT JOIN document_embedding de ON c.uri = de.uri
)
SELECT * FROM features;
```

---

## Latency vs Quality Tradeoffs

### Latency Benchmarks

| Component | Typical Latency | Notes |
|-----------|----------------|-------|
| BM25 retrieval (100 docs) | 1-10ms | Depends on index size |
| Vector search HNSW (100 docs) | 5-20ms | Depends on dimension, ef_search |
| RRF fusion | <1ms | Simple arithmetic |
| Cross-encoder (batch of 10) | 60ms | MiniLM-L6-v2, CPU |
| Cross-encoder (batch of 50) | 350ms | MiniLM-L6-v2, CPU |
| Cross-encoder (batch of 100) | 740ms | MiniLM-L6-v2, CPU |
| LTR inference (100 docs) | 1-5ms | XGBoost, feature vectors ready |

### Latency Optimization Strategies

1. **Reduce reranking depth**: Rerank top-50 instead of top-100 (2x speedup)
2. **Use smaller cross-encoders**: TinyBERT vs MiniLM (4x speedup)
3. **Batch efficiently**: Process all candidates in single forward pass
4. **Use GPU**: 5-10x speedup for cross-encoders
5. **Quantize models**: INT8 quantization (2-3x speedup, ~2% quality loss)
6. **Shallow cross-encoders**: 2-4 transformer layers (acceptable under 50ms budgets)
7. **Early termination**: Skip reranking if Stage 1 confidence is high

### Quality vs Latency Curves

```
Quality (NDCG@10)
    |
1.0 +                              * LLM Reranker (4-6s)
    |                         * Cross-encoder large (500ms)
    |                    * Cross-encoder base (200ms)
0.9 +               * Cross-encoder small (60ms)
    |          * LTR + features (50ms)
    |     * Hybrid RRF (30ms)
0.8 + * Dense only (20ms)
    |* BM25 only (5ms)
    +----+----+----+----+----+----+----+-----> Latency (ms)
         50   100  200  500  1000 2000 5000
```

### Recommended Configurations by Use Case

| Use Case | Target Latency | Configuration |
|----------|---------------|---------------|
| Autocomplete | <50ms | BM25 + dense, RRF, no rerank |
| Interactive search | <200ms | Hybrid + small cross-encoder (top-30) |
| Background analysis | <2s | Hybrid + large cross-encoder (top-100) |
| Batch processing | Any | Full pipeline with LLM reranker |

### User Experience Thresholds

Research suggests:
- **100ms**: Feels instant, ideal for autocomplete
- **200ms**: Noticeable but acceptable for search
- **500ms**: Starting to feel slow
- **3000ms**: Users begin abandoning searches

---

## Online Learning and Contextual Bandits

Once a two-stage ranking system is deployed, it can learn from user interactions to improve over time.

### The Exploration-Exploitation Dilemma

In online learning to rank, the system must balance:
- **Exploitation**: Show results the current model thinks are best
- **Exploration**: Show diverse results to learn about alternatives

The system only receives feedback on shown results. To learn about potentially better documents, it must occasionally show them--but showing poor results hurts user experience.

### Contextual Bandits Framework

Online LTR can be modeled as a contextual bandit problem:
- **Context**: Query features, user features, session state
- **Actions**: Ranked list of documents to show
- **Reward**: Click-through rate, dwell time, task completion

### Key Algorithms

| Algorithm | Approach | Notes |
|-----------|----------|-------|
| **Epsilon-greedy** | Random exploration with probability epsilon | Simple but inefficient |
| **UCB (Upper Confidence Bound)** | Explore uncertain items | Good theoretical guarantees |
| **Thompson Sampling** | Bayesian uncertainty sampling | Often best in practice |
| **Safe Exploration Algorithm (SEA)** | Never worse than baseline | Production-safe |

### Safe Exploration Algorithm

SEA addresses the key production concern: never showing results worse than the current system.

```
1. Start with baseline (production) ranker
2. Use counterfactual learning to train new policy on baseline's behavior
3. Deploy new policy only when confident it matches or exceeds baseline
4. New policy can now explore in favorable regions
5. Continue learning from new policy's behavior
```

### Implicit Feedback Signals

| Signal | Interpretation | Challenges |
|--------|---------------|------------|
| Click | Interest | Position bias, attractive snippets |
| Dwell time | Relevance | Depends on content length |
| Return click | First result unsatisfying | May indicate good exploration |
| Query reformulation | Original results poor | Natural behavior variation |
| Copy/paste | Found useful content | Hard to capture |
| Task completion | Success | Hard to define/measure |

### Position Bias Correction

Users are more likely to click higher-ranked results regardless of relevance. Correction methods:

1. **Propensity weighting**: Weight feedback by inverse of position click probability
2. **Pairwise preference**: Compare clicks at similar positions
3. **Interleaving**: Randomly interleave results from two rankers

### Practical Considerations for RepoQL

For agent-facing code search tools, relevant signals include:
- Which file was ultimately opened/read
- Whether the user re-queried with different terms
- Which results were included in agent output
- Task completion (if measurable)

---

## Best Practices and Common Pitfalls

### Best Practices

1. **Start simple, add complexity gradually**
   - Begin with hybrid search + RRF
   - Add cross-encoder reranking if quality insufficient
   - Add LTR features only when needed

2. **Tune retrieval depth empirically**
   - Recommended starting point: top-100 retrieval, top-10-20 after reranking
   - Measure recall@k for different k values
   - Diminishing returns beyond top-75 in most applications

3. **Match train and inference distributions**
   - LTR models should be trained on candidates from the same retrieval system
   - Re-train when retrieval components change

4. **Monitor and log everything**
   - Log retrieved candidates, scores, and final rankings
   - Track metrics at each pipeline stage
   - Enable A/B testing infrastructure early

5. **Handle edge cases**
   - Empty results: Fall back to broader retrieval
   - Timeout: Return Stage 1 results without reranking
   - Model errors: Graceful degradation to base retrieval

6. **Use consistent score scales**
   - Cross-encoders output 0-1 scores; use these for thresholding
   - Normalize scores when combining multiple sources

### Common Pitfalls

| Pitfall | Impact | Solution |
|---------|--------|----------|
| Reranking too many documents | Latency blowup | Limit to top-50-100 |
| Reranking too few documents | Missing relevant results | Ensure recall@k is high first |
| Ignoring position bias | Learning wrong relevance | Use debiasing techniques |
| Training LTR on different retrieval | Distribution shift | Train on actual pipeline candidates |
| Not batching cross-encoder | Unnecessary latency | Always batch inference |
| Using cross-encoder for retrieval | Impossible latency | Cross-encoder is reranking only |
| Ignoring input limits | Truncation artifacts | Chunk or summarize long documents |
| Over-relying on neural scores | Missing exact matches | Include BM25/sparse features |

### Code Search Specific Pitfalls

| Pitfall | Impact | Solution |
|---------|--------|----------|
| Ignoring identifier matching | Missing exact symbol names | Weighted BM25F on symbol fields |
| Treating all code equally | Test files ranked with production | Penalize test/generated code |
| Ignoring graph structure | Missing related code | Include call graph features |
| Recency bias too strong | Missing stable foundational code | Balance recency with stability |
| Ignoring file type | Mixing code, config, docs | Type-aware scoring/filtering |

---

## References

### Foundational Papers

1. **RankNet, LambdaRank, LambdaMART**
   - Burges, C. J. C. (2010). *From RankNet to LambdaRank to LambdaMART: An Overview*. Microsoft Research Technical Report MSR-TR-2010-82.
   - [Paper PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/MSR-TR-2010-82.pdf)
   - [Microsoft Research Publication](https://www.microsoft.com/en-us/research/publication/from-ranknet-to-lambdarank-to-lambdamart-an-overview/)

2. **Learning to Rank for Information Retrieval**
   - Liu, T. Y. (2011). *Learning to Rank for Information Retrieval*. Foundations and Trends in Information Retrieval.
   - [Microsoft Research](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/LambdaMART_Final.pdf)

3. **RankNet Original Paper**
   - Burges, C. et al. (2005). *Learning to Rank using Gradient Descent*. ICML 2005.
   - [RankNet Retrospective](https://www.microsoft.com/en-us/research/blog/ranknet-a-ranking-retrospective/)

### Cross-Encoder and Reranking

4. **Cross-Encoder Sentence Transformers**
   - Reimers, N., & Gurevych, I. (2019). *Sentence-BERT: Sentence Embeddings using Siamese BERT-Networks*.
   - [Sentence Transformers Documentation](https://sbert.net/examples/applications/cross-encoder/README.html)
   - [Retrieve & Re-Rank](https://sbert.net/examples/sentence_transformer/applications/retrieve_rerank/README.html)

5. **Search Reranking with Cross-Encoders**
   - OpenAI Cookbook Example
   - [OpenAI Cookbook](https://cookbook.openai.com/examples/search_reranking_with_cross-encoders)

6. **Shallow Cross-Encoders for Low-Latency Retrieval**
   - [arXiv:2403.20222](https://arxiv.org/html/2403.20222v1)

### Reranker Models

7. **BGE Reranker**
   - BAAI (2024). *BGE Reranker Models*.
   - [bge-reranker-base](https://huggingface.co/BAAI/bge-reranker-base)
   - [bge-reranker-v2-m3](https://huggingface.co/BAAI/bge-reranker-v2-m3)

8. **Rerankers Library**
   - AnswerDotAI (2024). *Unified API for Reranking Models*.
   - [GitHub Repository](https://github.com/AnswerDotAI/rerankers)

9. **Cohere Rerank**
   - [Cohere Documentation](https://docs.cohere.com/docs/rerank)

10. **Voyage AI Rerankers**
    - [Voyage AI Blog](https://blog.voyageai.com/2024/03/15/boosting-your-search-and-rag-with-voyages-rerankers/)

### Hybrid Search and Fusion

11. **Reciprocal Rank Fusion**
    - Cormack, G. V., Clarke, C. L., & Buettcher, S. (2009). *Reciprocal Rank Fusion Outperforms Condorcet and Individual Rank Learning Methods*. SIGIR 2009.
    - [Paper PDF](https://cormack.uwaterloo.ca/cormacksigir09-rrf.pdf)

12. **Two-Stage Retrieval with Reranking**
    - [MyScale Blog](https://medium.com/@myscale/two-stage-retrieval-with-reranking-functions-and-myscale-3e9beada1782)

13. **Pinecone Reranking Guide**
    - [Pinecone Learn](https://www.pinecone.io/learn/series/rag/rerankers/)

### Online Learning to Rank

14. **Contextual Bandits for Information Retrieval**
    - Hofmann, K., Whiteson, S., & de Rijke, M. (2013). *Contextual Bandits for Information Retrieval*.
    - [Paper PDF](https://www.cs.ubc.ca/~hutter/nips2011workshop/papers_and_posters/nips-2012-rl4ir.pdf)
    - [Oxford Publication](https://www.cs.ox.ac.uk/publications/publication9813-abstract.html)

15. **Safe Exploration for Optimizing Contextual Bandits**
    - [ACM TOIS](https://dl.acm.org/doi/10.1145/3385670)

16. **Cascading Hybrid Bandits**
    - [ACM RecSys 2020](https://dl.acm.org/doi/10.1145/3383313.3412245)

### DuckDB Implementation

17. **DuckDB Vector Similarity Search**
    - [DuckDB VSS Extension](https://duckdb.org/docs/stable/core_extensions/vss)
    - [VSS Blog Post](https://duckdb.org/2024/05/03/vector-similarity-search-vss)
    - [GitHub Repository](https://github.com/duckdb/duckdb-vss)

18. **MotherDuck Search Series**
    - [Building Vector Search](https://motherduck.com/blog/search-using-duckdb-part-1/)
    - [Integrating Full Text and Embedding Methods](https://motherduck.com/blog/search-using-duckdb-part-3/)

### Code-Specific Resources

19. **BM25 Probabilistic Framework**
    - Robertson, S., & Zaragoza, H. (2009). *The Probabilistic Relevance Framework: BM25 and Beyond*.
    - [Paper PDF](https://www.staff.city.ac.uk/~sbrp622/papers/foundations_bm25_review.pdf)

20. **LETOR Benchmark**
    - Liu, T. Y. et al. *LETOR: A Benchmark Collection for Research on Learning to Rank*.
    - [Microsoft Research](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/08/letor3.pdf)

21. **Code Churn for Defect Prediction**
    - Nagappan, N., & Ball, T. (2005). *Use of Relative Code Churn Measures to Predict System Defect Density*. ICSE 2005.
    - [Paper PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/icse05churn.pdf)

### LTR Implementation

22. **XGBoost Learning to Rank**
    - [XGBoost LTR Tutorial](https://xgboost.readthedocs.io/en/latest/tutorials/learning_to_rank.html)

23. **LlamaIndex RAG Evaluation**
    - [Boosting RAG: Picking the Best Embedding & Reranker Models](https://www.llamaindex.ai/blog/boosting-rag-picking-the-best-embedding-reranker-models-42d079022e83)

### Recent Surveys and Benchmarks

24. **Evolution of Reranking Models**
    - [arXiv:2512.16236](https://arxiv.org/html/2512.16236v1)

25. **RankArena: Unified Evaluation Platform**
    - [arXiv:2508.05512](https://arxiv.org/html/2508.05512v1)

26. **LLMs for Reranking Comparison**
    - [ZeroEntropy Blog](https://www.zeroentropy.dev/articles/should-you-use-llms-for-reranking-a-deep-dive-into-pointwise-listwise-and-cross-encoders)

27. **Choosing the Best Reranking Model in 2025**
    - [ZeroEntropy Guide](https://www.zeroentropy.dev/articles/ultimate-guide-to-choosing-the-best-reranking-model-in-2025)
