# Vector Embeddings for Retrieval Systems

This document provides comprehensive coverage of vector embeddings as they apply to retrieval and ranking systems, with particular focus on code and document retrieval scenarios relevant to RepoQL.

## Table of Contents

1. [Fundamentals](#fundamentals)
2. [Retrieval Architecture](#retrieval-architecture)
3. [Ranking and Relevance](#ranking-and-relevance)
4. [Quality Considerations](#quality-considerations)
5. [Practical Considerations](#practical-considerations)
6. [Code-Specific Considerations](#code-specific-considerations)
7. [References](#references)

---

## Fundamentals

### What Are Vector Embeddings?

Vector embeddings are numerical representations of data (text, code, images) in a high-dimensional space where semantic similarity is preserved as geometric proximity. They transform discrete tokens into continuous vectors where similar concepts cluster together, enabling mathematical operations on meaning.

**Key Properties:**
- Fixed-dimensional output (e.g., 384, 768, 1024 dimensions)
- Semantically similar items produce similar vectors
- Enable approximate nearest neighbor search
- Support algebraic operations (vector arithmetic)

### Dense vs Sparse Embeddings

| Aspect | Dense Embeddings | Sparse Embeddings |
|--------|------------------|-------------------|
| **Structure** | Compact vectors, most dimensions non-zero | High-dimensional, most dimensions zero |
| **Generation** | Neural networks (BERT, GPT, sentence transformers) | TF-IDF, BM25, SPLADE |
| **Dimensions** | 384-4096 typical | Vocabulary size (50k-100k+) |
| **Semantics** | Captures nuanced relationships, context | Captures exact term matches |
| **Interpretability** | Low (latent features) | High (each dimension = specific term) |
| **Best For** | Semantic similarity, paraphrasing | Keyword matching, exact terms |

**Practical Guidance:**
- Dense embeddings excel at finding conceptually related content even when wording differs
- Sparse embeddings excel at exact term matching and handle domain-specific jargon better
- Modern systems combine both approaches (hybrid search) for best results

### Embedding Dimensions and Tradeoffs

Embedding dimension directly affects quality, latency, and storage:

| Dimension | Memory Impact | Latency Impact | Quality |
|-----------|---------------|----------------|---------|
| 384 | Baseline | Fastest | Good for general use |
| 768 | 2x baseline | ~20% slower | Better semantic distinction |
| 1024 | 2.7x baseline | ~40% slower | Best for specialized domains |
| 1536+ | 4x+ baseline | ~50%+ slower | Diminishing returns |

**Key Findings:**
- For most content, accuracy improvements flatten between 768 and 1024 dimensions
- A 384-dimensional model might conflate nuanced terms (e.g., "patient remission" vs "disease recurrence")
- Technical/specialized domains (code, legal, medical) often benefit from 1024+ dimensions
- Storage: 1536-dim requires 4x more memory than 384-dim for same dataset

**Recommendation:** Start with 384-dim for speed/scale optimization. Upgrade to 768-1024 when semantic precision is critical. The 1024 dimension appears to be a sweet spot for many applications, providing near-maximum quality at reasonable resource cost.

### Similarity Metrics

The choice of similarity metric should match how the embedding model was trained:

#### Cosine Similarity

```
cosine(A, B) = (A . B) / (||A|| * ||B||)
```

- Measures angle between vectors, ignoring magnitude
- Range: [-1, 1] where 1 = identical direction
- **Use when:** Direction matters more than magnitude; NLP/text similarity
- **Common for:** Sentence transformers, most text embedding models

#### Dot Product

```
dot(A, B) = A . B = sum(a_i * b_i)
```

- Measures both direction and magnitude
- Unbounded range
- **Use when:** Magnitude encodes importance (e.g., popularity in recommendations)
- **For normalized embeddings:** Equivalent to cosine similarity (faster to compute)

#### Euclidean Distance

```
euclidean(A, B) = sqrt(sum((a_i - b_i)^2))
```

- Measures absolute distance in space
- Range: [0, infinity) where 0 = identical
- **Use when:** True geometric distance matters; clustering (K-means)
- **Common for:** LSH-based models, count-based features

**Critical Rule:** Match the metric to your model's training objective. Most modern text embedding models (OpenAI, BGE, E5) produce normalized embeddings where cosine and dot product are equivalent.

---

## Retrieval Architecture

### Bi-Encoder vs Cross-Encoder

Understanding these two architectural paradigms is fundamental to building effective retrieval systems.

#### Bi-Encoders

Bi-encoders encode queries and documents independently, producing separate embeddings that can be compared via similarity metrics.

```
Query  --> Encoder --> Query Embedding  --\
                                           }--> Similarity Score
Doc    --> Encoder --> Doc Embedding   --/
```

**Characteristics:**
- Document embeddings can be pre-computed and cached
- Sub-millisecond retrieval over millions of candidates
- Ideal for first-stage retrieval at scale
- Lower accuracy than cross-encoders (no cross-attention)

**Example Models:** all-MiniLM-L6-v2, BGE, E5, text-embedding-3-small

#### Cross-Encoders

Cross-encoders process query-document pairs together, allowing full attention between all tokens.

```
[Query + Document] --> Encoder --> Relevance Score
```

**Characteristics:**
- Must process every query-document pair at inference time
- Computationally expensive (scales poorly)
- Significantly higher accuracy (+4 NDCG@10 over bi-encoders)
- Ideal for reranking small candidate sets

**Example Models:** ms-marco-MiniLM-L-6-v2, BGE-reranker-large, Cohere rerank

### Two-Stage Retrieval (Retrieve then Rerank)

The dominant architecture for production retrieval systems:

```
Query
  |
  v
[Stage 1: Retrieval] -- Bi-encoder + ANN index
  |                     Fast, scalable
  | Top-100 candidates
  v
[Stage 2: Reranking] -- Cross-encoder
  |                     Accurate, expensive
  | Top-10 results
  v
Final Results
```

**Best Practices:**
- Retrieve 20-50 documents in stage 1, rerank to 5-10 for final use
- Reranking more than 50 documents shows diminishing returns and increases latency
- For general use: ms-marco-MiniLM-L-6-v2 (fast, accurate)
- For multilingual: BGE-reranker-large
- For highest accuracy: LLM-based reranking (10-50x cost increase)

### Multi-Vector Models (Late Interaction)

ColBERT and similar models represent a middle ground between bi-encoders and cross-encoders:

```
Query tokens  --> [q1, q2, ..., qn] --\
                                       }--> MaxSim --> Score
Doc tokens    --> [d1, d2, ..., dm] --/
```

**ColBERT Architecture:**
- Encodes each token independently (like bi-encoder)
- Computes token-level similarity at query time (late interaction)
- Uses sum-of-maximum-similarities (MaxSim) scoring
- Document embeddings can be pre-computed (128-dim per token)

**ColBERTv2 Improvements:**
- Aggressive quantization (256 bytes -> 36 bytes per vector)
- 2-bit compression maintains good accuracy
- Jina ColBERT v2: Multilingual (89 languages), flexible dimensions (64-128)

**Tradeoff:** Higher storage requirements (embedding per token) but better quality than single-vector bi-encoders while remaining scalable.

### Approximate Nearest Neighbor (ANN) Algorithms

Exact nearest neighbor search is O(n) per query. ANN algorithms trade small accuracy losses for dramatic speed improvements.

#### HNSW (Hierarchical Navigable Small World)

```
Layer 3:  o---o  (sparse, long-range connections)
          |
Layer 2:  o-o-o-o  (medium density)
          |
Layer 1:  o-o-o-o-o-o-o-o  (dense, local connections)
```

**How it works:**
1. Build multi-layer graph with proximity-based connections
2. Start search at top layer (sparse, long jumps)
3. Navigate down layers, refining search
4. Bottom layer provides final nearest neighbors

**Characteristics:**
| Property | Value |
|----------|-------|
| Recall | Very high (near brute-force) |
| Memory | High (graph structure overhead) |
| Build Time | Longer |
| Dynamic Updates | Good (incremental inserts) |
| Filtered Search | Less efficient |

**Best For:** Precision-critical applications, dynamic datasets with frequent updates

#### IVF (Inverted File Index)

```
Centroids:    C1    C2    C3    C4
              |     |     |     |
Clusters:   [...]  [...]  [...]  [...]
```

**How it works:**
1. Cluster data using k-means into partitions
2. At query time, identify nearest centroids
3. Search only within those partitions
4. Return approximate nearest neighbors

**Characteristics:**
| Property | Value |
|----------|-------|
| Recall | Moderate to high |
| Memory | Lower than HNSW |
| Build Time | Faster |
| Dynamic Updates | Requires retraining |
| Filtered Search | More efficient |

**Best For:** Large static datasets, systems with memory constraints, filtered search

#### Hybrid: IVF-HNSW

Combines both approaches:
- IVF partitions dataset into clusters
- HNSW performs fine-grained search within clusters
- Delivers scalability with strong recall
- Supported in FAISS and Milvus

#### Product Quantization (PQ)

Compression technique that reduces memory footprint:
- Splits vectors into subspaces
- Quantizes each subspace independently
- Reduces 768-dim float32 (3KB) to ~64 bytes
- Trades accuracy for massive storage savings

**When to use:** Billion-scale datasets where memory is the primary constraint.

### Vector Databases and Index Selection

| Database | Index Types | Best For |
|----------|-------------|----------|
| FAISS | IVF, HNSW, PQ, combinations | Research, batch processing |
| Pinecone | Proprietary (HNSW-based) | Managed cloud, simplicity |
| Milvus/Zilliz | IVF, HNSW, DiskANN | Self-hosted, scale |
| Qdrant | HNSW | Self-hosted, filtering |
| Weaviate | HNSW | Self-hosted, hybrid search |
| DuckDB VSS | HNSW | Embedded, analytical workloads |
| pgvector | IVF, HNSW | PostgreSQL integration |

---

## Ranking and Relevance

### Semantic Search with Embeddings

Pure semantic search workflow:

```
1. Query --> Embed --> Query Vector
2. Query Vector --> ANN Index --> Top-K Candidates
3. Candidates --> Return ranked by similarity
```

**Strengths:**
- Finds conceptually related content regardless of exact wording
- Handles synonyms, paraphrasing naturally
- Cross-lingual retrieval possible with multilingual models

**Weaknesses:**
- May miss exact keyword matches
- Struggles with domain-specific terminology
- Can return semantically related but irrelevant results

### Hybrid Search (Lexical + Semantic)

Combining BM25 (lexical) with vector search (semantic) consistently outperforms either alone:

```
Query
  |
  +--> BM25 Search     --> Lexical Results   --\
  |                                             }--> Fusion --> Final Ranking
  +--> Vector Search   --> Semantic Results  --/
```

#### Fusion Methods

**Reciprocal Rank Fusion (RRF):**
```
RRF_score(d) = sum(1 / (k + rank_i(d)))
```
- Combines by rank, not raw score
- No normalization required
- Robust baseline, works across domains
- k typically = 60

**Linear Combination:**
```
final_score = w1 * normalize(lexical_score) + w2 * normalize(semantic_score)
```
- Requires score normalization (min-max scaling)
- Allows tuning importance of each signal
- Can outperform RRF with proper calibration
- Dataset-specific tuning required

**RepoQL's Approach (from search.sql):**
```sql
bm25_weight := 0.15,    -- BM25 lexical score
fuzzy_weight := 0.15,   -- Fuzzy matching
semantic_weight := 0.70 -- Semantic similarity
```

### Three-Way Hybrid Search

State-of-the-art systems combine three retrieval methods:

1. **BM25** - Lexical precision
2. **Dense Vectors** - Semantic understanding
3. **Sparse Vectors** (SPLADE) - Learned term importance

Research shows this three-way hybrid with ColBERT reranking yields the best retrieval quality for RAG systems.

### Reranking Strategies

| Strategy | Latency | Quality | Cost | Use Case |
|----------|---------|---------|------|----------|
| Cross-encoder | ~50ms/doc | High | Low | General reranking |
| ColBERT | ~10ms/doc | High | Medium | Quality-latency balance |
| LLM-based | ~500ms/doc | Highest | High | High-stakes decisions |
| Cohere Rerank | ~20ms/doc | High | API cost | Enterprise SLA |

**Practical Limits:**
- Rerank top 20-50 candidates maximum
- Beyond 50, diminishing returns exceed latency cost
- Cross-encoder quality drops off after 20-30 candidates

### Relevance Scoring Calibration

Raw similarity scores are not probabilities. Calibration approaches:

1. **Score Normalization:** Min-max scaling within result set
2. **Learned Calibration:** Train mapping from scores to relevance
3. **Threshold-based:** Define cutoffs based on historical data
4. **Ensemble Voting:** Use agreement between methods as confidence signal

RepoQL uses a 5% boost when structure and full-text embeddings agree (reinforcement signal).

---

## Quality Considerations

### Evaluation Metrics

#### Recall@K

```
Recall@K = (Relevant items in top K) / (Total relevant items)
```

- Not rank-aware (position doesn't matter)
- Critical for RAG: Low recall = missing necessary facts
- Target: 90%+ Recall@10 for FAQ/chatbot systems

#### Mean Reciprocal Rank (MRR)

```
MRR = (1/|Q|) * sum(1/rank_i)
```

- Rank-aware: Rewards finding correct answer early
- MRR=1.0 if first result is always correct
- Good for single-answer queries

#### Normalized Discounted Cumulative Gain (NDCG)

```
DCG@K = sum(relevance_i / log2(i + 1))
NDCG@K = DCG@K / IDCG@K
```

- Accounts for graded relevance (not just binary)
- Penalizes relevant results appearing late
- Default metric on MTEB leaderboard
- Target: NDCG@10 > 0.8 for high-quality retrieval

#### RAG-Specific Considerations

Recent research indicates traditional metrics (NDCG, MAP, MRR) may not predict RAG performance well because they assume monotonically decreasing utility with rank. New metrics like UDCG show 36% better correlation with end-to-end RAG accuracy.

### Domain Adaptation and Fine-Tuning

Off-the-shelf embedding models are trained on general corpora (Wikipedia, web crawl). Domain-specific data requires adaptation:

**Performance Gains from Fine-Tuning:**
- General improvement: ~7% with only 6.3k training samples
- Semantic search: Up to 10 points improvement with GPL
- NDCG@10: 0.5949 -> 0.8245 in one documented case

**Adaptation Strategies:**

1. **Continued Pre-training (TSDAE):**
   - Pre-train on domain corpus with masked language modeling
   - Then fine-tune on labeled data
   - Up to 8 points improvement

2. **Generative Pseudo Labeling (GPL):**
   - Generate synthetic queries from documents using LLM
   - Create (query, positive, negative) triplets
   - Fine-tune with contrastive loss
   - Up to 10 points improvement on semantic search

3. **LoRA Adaptation:**
   - Low-rank adaptation matrices in attention layers
   - Efficient: Only adds ~1% parameters
   - Language-specific gains: 3.4-9.1% MRR improvement

**Practical Approach:**
```
1. Generate synthetic queries from your corpus using LLM
2. Filter for quality with LLM-as-judge
3. Create contrastive pairs (query + relevant doc + hard negatives)
4. Fine-tune base model (BGE, E5) with contrastive loss
5. Evaluate on held-out test set
```

### Out-of-Domain Performance

Cross-encoders significantly outperform bi-encoders in zero-shot (out-of-domain) settings, with 4+ points NDCG@10 advantage. For systems that must handle diverse domains without fine-tuning:

1. Use cross-encoder reranking (more robust OOD)
2. Prefer larger embedding models (better generalization)
3. Implement hybrid search (lexical helps with novel terms)
4. Monitor performance across domain segments

### Embedding Drift and Maintenance

**What is Embedding Drift?**
Same text produces different embeddings over time due to:
- Preprocessing changes
- Model version updates
- Partial corpus re-embedding
- Text normalization differences

**Impact:** Semantically identical text with structurally different vectors causes unstable retrieval.

**Prevention Strategies:**

1. **Pin Model Versions:** No silent updates to embedding models
2. **Deterministic Preprocessing:** Identical rules for whitespace, unicode, markdown
3. **Never Mix Versions:** Don't mix embeddings from different model versions
4. **Version Embeddings:** Store snapshots for rollback capability
5. **Monitor Drift:** Track cosine similarity between old/new embeddings

**Drift-Adapter Approach:**
When upgrading models, train a lightweight transformation layer to map new queries into the legacy embedding space, recovering 95-99% of retrieval performance with 100x less compute than full re-indexing.

**CI/CD Integration:**
- Detect drift in embeddings pipeline
- Trigger reindexing when recall drops below threshold
- Visualize with t-SNE/UMAP cluster comparisons

---

## Practical Considerations

### Latency vs Quality Tradeoffs

| Configuration | Latency | Quality | Use Case |
|---------------|---------|---------|----------|
| HNSW + bi-encoder | <50ms | Good | Real-time search |
| HNSW + reranker | 50-200ms | Better | User-facing search |
| IVF-PQ + bi-encoder | <100ms | Moderate | Cost-sensitive scale |
| Full retrieval + LLM rerank | 1-5s | Best | High-stakes decisions |

**Guidelines:**
- Real-time retrieval: Sub-50ms embedding generation + sub-50ms ANN search
- Interactive: <500ms total retrieval pipeline
- Batch/offline: Optimize for throughput over latency

### Batching Strategies

Efficient batching is critical for embedding throughput:

**Token-Count Based Batching:**
- Batch by total tokens, not document count
- Avoids padding waste from variable-length documents
- Optimal batch size depends on GPU memory and model

**Length-Based Sorting:**
- Sort documents by token count before batching
- Group similar lengths together
- Minimizes padding overhead

**Production Results:**
- MongoDB/Voyage AI: 50% latency reduction with proper batching
- Snowflake/vLLM: 16x throughput improvement for short sequences

**Best Practices:**
```
1. Pre-tokenize inputs (disaggregate tokenization from inference)
2. Sort by token count
3. Batch to target token count (not document count)
4. Run multiple model replicas per GPU for small models
5. Use FP8 quantization for 50%+ throughput gain
```

### Caching Embeddings

**Query Embedding Cache:**
- Cache query embeddings (they repeat often)
- Semantic cache: Match semantically similar queries
- Can skip entire embedding + retrieval pipeline on cache hit

**Document Embedding Storage:**
- Pre-compute and persist document embeddings
- Store with metadata (model version, timestamp)
- Enable incremental updates

**Semantic Caching Benefits:**
- Embedding: ~200ms, Retrieval: ~5ms, Synthesis: ~6s
- Caching avoids the expensive synthesis step (1200x slower than retrieval)
- Different wordings of same query can hit cache

### Incremental Indexing

Full re-indexing is expensive. Incremental strategies:

**Bidirectional Lineage Tracking:**
```
Forward:  source -> chunks -> embeddings -> vector IDs
Backward: vector ID -> embedding -> chunk -> source
```

Enables precise updates and deletions without full rebuild.

**Cost Comparison (12k file documentation site):**
| Operation | Time | API Cost | Vector Writes |
|-----------|------|----------|---------------|
| Full reindex | 22 min | $8.50 | 50,000 |
| Incremental (10 files) | 45 sec | $0.07 | 400 |

**Implementation Requirements:**
- Track source file -> embedding relationships
- Detect changed files efficiently (checksums, timestamps)
- Support deletion cascade when sources are removed
- Handle chunk boundary changes gracefully

### Memory and Storage Optimization

**Quantization Options:**
| Technique | Compression | Quality Loss |
|-----------|-------------|--------------|
| FP32 -> FP16 | 2x | Minimal |
| FP32 -> INT8 | 4x | Small |
| FP32 -> INT4/PQ | 8-16x | Moderate |
| Binary quantization | 32x | Significant |

**Storage Modes:**
- In-memory: Fastest, most expensive
- Memory-mapped: Good balance, uses OS page cache
- On-disk: Lowest cost, highest latency

**For large datasets:** Combine IVF (partitioning) with PQ (compression) for billion-scale with reasonable resources.

---

## Code-Specific Considerations

### Code Embedding Models

| Model | Dimensions | Languages | Best For |
|-------|------------|-----------|----------|
| UniXcoder | 768 | 6 | Code search, understanding |
| CodeBERT | 768 | 6 | Code-NL matching |
| GraphCodeBERT | 768 | 6 | Structure-aware tasks |
| CodeSage Large V2 | varies | Many | General code understanding |
| Nomic Embed Code | 768 | Many | Code retrieval |
| VoyageCode3 | 1024 | Many | Code understanding |

**Key Findings:**
- UniXcoder outperforms CodeBERT due to encoder-decoder architecture
- Decoder-only LLMs (not optimized for next-token prediction) produce embeddings that don't align well with code search needs
- LoRA fine-tuning on UniXcoder yields 3-9% MRR improvement across languages

### Code vs Text Retrieval Differences

| Aspect | Text | Code |
|--------|------|------|
| Vocabulary | Natural language | Keywords + identifiers |
| Structure | Paragraphs, sentences | AST, control flow |
| Semantics | Meaning from context | Behavior from execution |
| Queries | Natural language | Mix of NL + code patterns |

**Recommendations for Code Retrieval:**
1. Use code-specific embedding models (not general text models)
2. Combine with BM25 for exact identifier matching
3. Consider multi-modal: embed code + documentation together
4. Handle multiple languages in same codebase
5. Leverage AST/structure information when available

### Hybrid Search for Code

```
Query: "JWT token validation"
  |
  +--> BM25: Exact matches on "JWT", "token", "validation"
  |
  +--> Semantic: Conceptually related auth code
  |
  +--> Structure: Functions named *Validate*, *Token*, etc.
  |
  v
Fusion -> Rerank -> Results
```

RepoQL's approach combines:
- Semantic search on document summaries (structure embeddings)
- Semantic search on full content (full embeddings)
- BM25 lexical matching
- Fuzzy matching for typos/partial matches

---

## References

### Fundamentals
- [Sparse embeddings: Dense vs sparse vector](https://www.elastic.co/search-labs/blog/sparse-vector-embedding) - Elasticsearch Labs
- [What are dense and sparse embeddings?](https://milvus.io/ai-quick-reference/what-are-dense-and-sparse-embeddings) - Milvus
- [Sparse and Dense Embeddings](https://zilliz.com/learn/sparse-and-dense-embeddings) - Zilliz Learn

### Architecture
- [Bi-Encoder and Cross-Encoder Architectures](https://www.emergentmind.com/topics/bi-encoder-and-cross-encoder-architectures) - Emergent Mind
- [Retrieve & Re-Rank](https://sbert.net/examples/sentence_transformer/applications/retrieve_rerank/README.html) - Sentence Transformers
- [A Survey of Model Architectures in Information Retrieval](https://arxiv.org/html/2502.14822v2) - arXiv

### ANN Algorithms
- [Approximate Nearest Neighbor (ANN) Search Explained: IVF vs HNSW vs PQ](https://www.pingcap.com/article/approximate-nearest-neighbor-ann-search-explained-ivf-vs-hnsw-vs-pq/) - TiDB
- [How to Choose Between IVF and HNSW](https://milvus.io/blog/understanding-ivf-vector-index-how-It-works-and-when-to-choose-it-over-hnsw.md) - Milvus
- [Hierarchical Navigable Small Worlds (HNSW)](https://www.pinecone.io/learn/series/faiss/hnsw/) - Pinecone

### Hybrid Search
- [Hybrid Search Explained](https://weaviate.io/blog/hybrid-search-explained) - Weaviate
- [Hybrid Search in PostgreSQL: The Missing Manual](https://www.paradedb.com/blog/hybrid-search-in-postgresql-the-missing-manual) - ParadeDB
- [Building effective hybrid search in OpenSearch](https://opensearch.org/blog/building-effective-hybrid-search-in-opensearch-techniques-and-best-practices/) - OpenSearch

### Evaluation
- [Evaluation Metrics for Search and Recommendation Systems](https://weaviate.io/blog/retrieval-evaluation-metrics) - Weaviate
- [Evaluation Measures in Information Retrieval](https://www.pinecone.io/learn/offline-evaluation/) - Pinecone
- [RAG Evaluation Metrics Explained](https://langcopilot.com/posts/2025-09-17-rag-evaluation-101-from-recall-k-to-answer-faithfulness) - LLM Practical Experience Hub

### Domain Adaptation
- [Why, When and How to Fine-Tune a Custom Embedding Model](https://weaviate.io/blog/fine-tune-embedding-model) - Weaviate
- [Improving Retrieval and RAG with Embedding Model Finetuning](https://www.databricks.com/blog/improving-retrieval-and-rag-embedding-model-finetuning) - Databricks
- [Domain Adaptation](https://sbert.net/examples/sentence_transformer/domain_adaptation/README.html) - Sentence Transformers

### Code Embeddings
- [6 Best Code Embedding Models Compared](https://modal.com/blog/6-best-code-embedding-models-compared) - Modal
- [UniXcoder](https://github.com/microsoft/CodeBERT/blob/master/UniXcoder/README.md) - Microsoft
- [LoRACode: LoRA Adapters for Code Embeddings](https://arxiv.org/pdf/2503.05315) - arXiv

### Optimization
- [High-performance embedding model inference](https://www.baseten.co/resources/guide/high-performance-embedding-model-inference/) - Baseten
- [Scaling vLLM for Embeddings](https://medium.com/snowflake/scaling-vllm-for-embeddings-16x-throughput-and-cost-reduction-f2b4d4c8e1bf) - Snowflake
- [Token-count-based Batching](https://www.mongodb.com/company/blog/engineering/token-count-based-batching-faster-cheaper-embedding-inference-for-queries) - MongoDB

### Drift and Maintenance
- [Embedding Drift: The Quiet Killer of Retrieval Quality](https://dev.to/dowhatmatters/embedding-drift-the-quiet-killer-of-retrieval-quality-in-rag-systems-4l5m) - DEV Community
- [Drift-Adapter: Near Zero-Downtime Embedding Model Upgrades](https://arxiv.org/html/2509.23471) - arXiv

### Similarity Metrics
- [Vector Similarity Explained](https://www.pinecone.io/learn/vector-similarity/) - Pinecone
- [Distance Metrics in Vector Search](https://weaviate.io/blog/distance-metrics-in-vector-search) - Weaviate

---

*Last updated: January 2026*
