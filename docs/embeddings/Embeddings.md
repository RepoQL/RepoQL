# Vector Embeddings for RepoQL

> Reference documentation for semantic search and retrieval in RepoQL

## Overview

**Vector embeddings** transform text and code into numerical representations where semantic similarity is preserved as geometric proximity. RepoQL uses embeddings to power semantic search, enabling queries like "authentication flow" to find relevant code even when the exact terms don't appear.

## Architecture

RepoQL's embedding pipeline:

```
┌─────────────────────────────────────────────────────────────┐
│                    Embedding Pipeline                        │
├─────────────────┬─────────────────┬─────────────────────────┤
│   Indexing      │   Storage       │   Query                 │
├─────────────────┼─────────────────┼─────────────────────────┤
│ Document chunks │ Vector index    │ Query embedding         │
│ → Tokenization  │ (per artifact)  │ → ANN search            │
│ → ONNX inference│                 │ → Hybrid fusion         │
│ → Normalization │                 │ → Reranking (optional)  │
└─────────────────┴─────────────────┴─────────────────────────┘
```

| Stage | Purpose |
|-------|---------|
| **Indexing** | Convert documents/code to embeddings during file processing |
| **Storage** | Store embeddings in DuckDB alongside graph data |
| **Query** | Embed query, find similar documents via ANN search |
| **Ranking** | Combine semantic + lexical scores for hybrid search |

## Core Concepts

### Embedding Models

| Model Type | Use Case | Example |
|------------|----------|---------|
| **Local (ONNX)** | Fast, private, no API cost | E5, BGE, all-MiniLM |
| **API-based** | Higher quality, code-specific | Voyage AI, OpenAI |
| **Hybrid** | Best of both | Local for bulk, API for queries |

### Similarity Search

```sql
-- RepoQL semantic search
SELECT * FROM search('authentication JWT refresh', k := 10);

-- Hybrid search (semantic + lexical)
SELECT * FROM search('config', scope := 'file:///src/**');
```

### Key Tradeoffs

| Dimension | Small (384) | Medium (768) | Large (1024+) |
|-----------|-------------|--------------|---------------|
| Quality | Good | Better | Best |
| Speed | Fastest | ~20% slower | ~40% slower |
| Memory | Baseline | 2x | 2.7x+ |
| Use Case | High volume | Balanced | Precision-critical |

## Documentation Structure

### Fundamentals

| Document | Description |
|----------|-------------|
| [Fundamentals](Fundamentals.md) | Embedding theory, similarity metrics, retrieval architecture |

### Model Options

| Document | Description |
|----------|-------------|
| [E5](E5.md) | Microsoft's E5 models (open-source, ONNX-ready) |
| [Voyage AI](VoyageAI.md) | API-based embeddings including voyage-code-3 |

### Implementation

| Document | Description |
|----------|-------------|
| [ONNX](ONNX.md) | Running embedding models locally with ONNX Runtime |

## Quick Reference

### Model Comparison

| Model | Dimensions | Context | Best For | Deployment |
|-------|------------|---------|----------|------------|
| e5-small-v2 | 384 | 512 | Fast local inference | ONNX |
| e5-base-v2 | 768 | 512 | Balanced quality/speed | ONNX |
| e5-large-v2 | 1024 | 512 | Highest local quality | ONNX |
| voyage-code-3 | 256-2048 | 32K | Code retrieval | API |
| voyage-3.5-lite | 1024 | 32K | Cost-effective API | API |

### Similarity Metrics

| Metric | Formula | Use When |
|--------|---------|----------|
| **Cosine** | `A·B / (‖A‖·‖B‖)` | Most text embeddings (direction matters) |
| **Dot Product** | `A·B` | Normalized embeddings (faster) |
| **Euclidean** | `√Σ(aᵢ-bᵢ)²` | Clustering, count-based features |

### Retrieval Patterns

| Pattern | Description | Latency | Quality |
|---------|-------------|---------|---------|
| **Semantic only** | Embedding similarity | Low | Good |
| **Lexical only** | BM25/keyword match | Lowest | Exact matches |
| **Hybrid** | Combine both scores | Medium | Better |
| **Two-stage** | Retrieve → Rerank | Higher | Best |

## Model Selection Guide

### For Code Search (RepoQL Primary Use Case)

```
┌─────────────────────────────────────────────────────────────┐
│                   Decision Tree                              │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Need API-level quality for code?                           │
│  ├─ Yes → voyage-code-3 (best code embeddings)              │
│  └─ No                                                       │
│      │                                                       │
│      └─ Running locally with ONNX?                          │
│          ├─ Yes, need speed → e5-small-v2 (384d)            │
│          ├─ Yes, balanced → e5-base-v2 (768d)               │
│          └─ Yes, max quality → e5-large-v2 (1024d)          │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Resource Requirements

| Model | Memory (FP32) | Memory (INT8) | Latency (CPU) |
|-------|---------------|---------------|---------------|
| e5-small-v2 | ~130 MB | ~35 MB | ~15ms |
| e5-base-v2 | ~440 MB | ~110 MB | ~30ms |
| e5-large-v2 | ~1.3 GB | ~335 MB | ~80ms |
| voyage-code-3 | N/A (API) | N/A | ~90ms + network |

## Best Practices

### Indexing

1. **Chunk documents** appropriately (code: by function/class; docs: by section)
2. **Batch embeddings** for throughput (32-64 items per batch)
3. **Cache embeddings** — don't re-embed unchanged content
4. **Use prefixes** for E5 models ("query: " and "passage: ")

### Search

1. **Hybrid search** outperforms pure semantic for code
2. **Limit k** to reasonable values (10-100) for ANN efficiency
3. **Rerank top results** if quality is critical
4. **Scope queries** when possible to reduce search space

### Production

1. **Quantize models** (INT8) for 2-4x memory reduction with ~2% quality loss
2. **Warm up sessions** to avoid cold-start latency
3. **Pool ONNX sessions** for concurrent requests
4. **Monitor embedding drift** if fine-tuning or updating models

## RepoQL Integration Points

| Component | Purpose |
|-----------|---------|
| `IEmbeddingProvider` | Abstraction for embedding generation |
| `search()` UDF | SQL interface to semantic search |
| `artifact.embedding` | Stored embedding vectors |
| `EmbedUdf` | Expose embedding functionality to SQL |

## External Resources

- [MTEB Leaderboard](https://huggingface.co/spaces/mteb/leaderboard) — Embedding model benchmarks
- [Hugging Face ONNX Models](https://huggingface.co/models?library=onnx) — Pre-converted models
- [Voyage AI Docs](https://docs.voyageai.com/) — API documentation
- [ONNX Runtime](https://onnxruntime.ai/) — Runtime documentation

---

*Embeddings turn meaning into math. Choose the right model for your tradeoffs.*
