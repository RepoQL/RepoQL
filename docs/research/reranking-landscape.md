# LLM Reranking: Landscape and Voyage AI

Research for evaluating reranking approaches — API services, local models, and integration patterns — with particular focus on Voyage AI's offering.

*Research date: March 5, 2026*

## Context

Reranking is the second stage of a retrieve-then-rerank pipeline: a fast first-stage retriever (BM25, vector search, or hybrid) returns broad candidates, then a more expensive reranker rescores and reorders them. This research informs decisions about whether and how to add reranking to a code-search pipeline, what technology to use, and the tradeoffs involved.

**Related research:** `docs/research/algorithms/TwoStageRanking.md` (architectural patterns), `docs/research/embeddings/VoyageAI.md` (Voyage embedding models).

---

## How Reranking Works

### The Pipeline

```
Query → First-stage retrieval (BM25 / vector / hybrid) → ~50-100 candidates
      → Reranker (cross-encoder / LLM) → Top-k reordered results
      → Consumer (LLM context, UI, etc.)
```

The first stage optimizes for **recall** (find everything relevant, cheaply). The reranker optimizes for **precision** (put the best results first, expensively). Cross-encoders process each (query, document) pair jointly through a transformer, capturing fine-grained interactions that bi-encoders miss.

> [ZeroEntropy Guide](https://www.zeroentropy.dev/articles/ultimate-guide-to-choosing-the-best-reranking-model-in-2025) — architecture overview

### Quality Impact

Reranking improves NDCG@10 by **15-40%** over first-stage retrieval alone. Specific findings:

- BM25 + reranking: **+39% NDCG** average across BEIR, pushing BM25 to ~position 20 on MTEB leaderboard ([Elastic blog](https://www.elastic.co/search-labs/blog/elastic-semantic-reranker-part-2))
- Databricks: up to **48%** improvement in retrieval quality ([Analytics Vidhya](https://www.analyticsvidhya.com/blog/2025/06/top-rerankers-for-rag/))
- MIT study: cross-encoder reranking improves RAG accuracy by **~40%** ([Ailog](https://app.ailog.fr/en/blog/news/reranking-cross-encoders-study))
- ZeroEntropy: **+28% NDCG@10** over baseline retrievers ([ZeroEntropy Guide](https://www.zeroentropy.dev/articles/ultimate-guide-to-choosing-the-best-reranking-model-in-2025))

Sweet spot: **50-75 candidates** for reranking balances quality with cost. Beyond 100, improvements plateau while costs increase linearly.

### When Reranking Helps Less

- Strong modern embedding models (Voyage-3-Large, BGE-M3, text-embedding-3-large) already surface most relevant results ([Fireworks blog](https://fireworks.ai/blog/Understanding-Embeddings-and-Reranking-at-Scale))
- Domain-specific retrieval that's already strong: "finetuned embeddings performed as well as or better than reranking in many cases" ([Databricks blog](https://www.databricks.com/blog/improving-retrieval-and-rag-embedding-model-finetuning))
- Documented cases where "rerankers provide no performance benefit at any depth compared to BM25 and show continuous degradation" ([Elastic blog](https://www.elastic.co/search-labs/blog/elastic-semantic-reranker-part-3))

The strongest systems combine both: fine-tuned embeddings + reranker yields best results.

### Taxonomy of Approaches

| Approach | Input | Mechanism | Tradeoffs |
|----------|-------|-----------|-----------|
| **Pointwise** | Single (query, doc) | Predict absolute relevance score | Simplest, fastest. Ignores inter-document relationships. Most cross-encoders use this. |
| **Pairwise** | Pair of (query, doc_a, doc_b) | Predict which is more relevant | Better than pointwise in practice. O(n²) pairs. |
| **Listwise** | Full list (query, [docs]) | Optimize entire ranking directly | Best quality. Most expensive. LLM-based rerankers (RankGPT) use this. |
| **Late interaction** | Token-level MaxSim | Pre-compute doc embeddings, score via token matching | ColBERT paradigm. Between bi-encoder speed and cross-encoder quality. |

> [Towards Data Science](https://towardsdatascience.com/ranking-basics-pointwise-pairwise-listwise-cd5318f86e1b/) — taxonomy overview
> [ZeroEntropy](https://www.zeroentropy.dev/articles/should-you-use-llms-for-reranking-a-deep-dive-into-pointwise-listwise-and-cross-encoders) — deep comparison

---

## Voyage AI

### Company

Founded September 2023 by Tengyu Ma (Stanford AI Lab). Team from Stanford, MIT, Berkeley, Princeton, CMU. Raised $28M total ($20M Series A, October 2024, led by CRV).

**Acquired by MongoDB for ~$220M in February 2025.** Now branded "Voyage AI by MongoDB."

**Not affiliated with Anthropic.** Anthropic is a customer — they recommend Voyage AI as their preferred embeddings provider since Claude doesn't offer embedding models. This is a recommendation, not an investment or partnership.

> [MongoDB Press Release](https://investors.mongodb.com/news-releases/news-release-details/mongodb-announces-acquisition-voyage-ai-enable-organizations) — acquisition
> [Inc.com](https://www.inc.com/chloe-aiello/voyage-ai-just-sold-for-220-million-after-launching-less-than-two-years-ago/91151766) — company background
> [Anthropic Embeddings Docs](https://docs.anthropic.com/en/docs/embeddings) — Anthropic recommendation

### Reranker Models

All models are cross-encoders (pointwise). Accept up to 1,000 documents per request.

| Model | Context | Max Query Tokens | Per 1M Tokens | Status |
|-------|---------|-----------------|---------------|--------|
| **rerank-2.5** | 32K | 8,000 | $0.05 | Current recommended |
| **rerank-2.5-lite** | 32K | 8,000 | $0.02 | Current recommended (fast) |
| rerank-2 | 16K | 4,000 | $0.05 | Legacy |
| rerank-2-lite | 8K | 2,000 | $0.02 | Legacy |
| rerank-1 | 8K | 2,000 | — | Legacy |

Free tier: **200M tokens** per account.

Token calculation: `(query_tokens × num_documents) + sum(document_tokens)`

Worked example: 100 documents averaging 500 tokens each + a 100-token query = (100 × 100) + 50,000 = 60,000 tokens per request. At $0.05/M, that's $0.003 per request, or **$3 per thousand requests**.

> [Voyage AI Reranker Docs](https://docs.voyageai.com/docs/reranker) — model specs
> [Voyage AI Pricing](https://docs.voyageai.com/docs/pricing) — pricing and token calculation

### API

`POST https://api.voyageai.com/v1/rerank` with Bearer token.

```json
// Request
{
  "query": "how does authentication work",
  "documents": ["doc1...", "doc2...", "..."],
  "model": "rerank-2.5",
  "top_k": 10,
  "return_documents": false
}

// Response
{
  "object": "list",
  "data": [
    { "index": 3, "relevance_score": 0.92 },
    { "index": 0, "relevance_score": 0.85 }
  ],
  "model": "rerank-2.5",
  "usage": { "total_tokens": 60000 }
}
```

Rate limits (Tier 1): 2,000 RPM, 2M TPM (rerank-2.5) / 4M TPM (lite). Higher tiers at $100+ and $1000+ spend.

Python SDK: `pip install voyageai` → `voyageai.Client().rerank(query, documents, model)`. No .NET SDK.

> [Voyage AI API Reference](https://docs.voyageai.com/reference/reranker-api) — endpoint details

### Key Differentiator: Instruction Following

rerank-2.5 (August 2025) introduced **instruction-following reranking** — natural language instructions steer relevance scoring. No competitor offered this at launch.

- Gains **8.13%** accuracy from using instructions (24 domain-specific datasets)
- **12.70%** improvement over Cohere Rerank 3.5 on MAIR instruction-following benchmark
- Enables: source credibility weighting, temporal relevance, domain-specific criteria, expertise-level filtering

> [Voyage AI Blog](https://blog.voyageai.com/2025/08/11/rerank-2-5/) — rerank-2.5 release
> [MongoDB Blog](https://www.mongodb.com/company/blog/technical/instruction-following-rerankers-an-unsung-context-engineering-tool) — instruction patterns

### Benchmarks

**rerank-2.5 vs competitors** (93 retrieval datasets, 9 domains, NDCG@10):
- Outperforms Cohere Rerank v3.5 by **7.94%**
- Outperforms Qwen3-Reranker-8B by **2.34%**
- Improves over rerank-2 by **1.85%**

**rerank-2 vs competitors** (93 datasets):
- Outperforms OpenAI v3 large by **13.89%**
- Outperforms Cohere v3 by **7.14%**
- Outperforms BGE v2-m3 by **15.61%**

**Independent (Agentset leaderboard):** Voyage Rerank 2.5 ranked near the top with ~595-603ms average latency. Described as "the most balanced choice for production use."

> [Voyage AI rerank-2 blog](https://blog.voyageai.com/2024/09/30/rerank-2/) — rerank-2 benchmarks
> [Voyage AI rerank-2.5 blog](https://blog.voyageai.com/2025/08/11/rerank-2-5/) — rerank-2.5 benchmarks
> [Agentset leaderboard](https://agentset.ai/rerankers) — independent comparison

**Source bias note:** Voyage AI's benchmark numbers are self-reported. The Agentset leaderboard is independently run but uses GPT-5 as judge (not standardized IR metrics). Head-to-head comparisons on identical benchmarks across vendors are scarce.

### Recent Timeline

| Date | Event |
|------|-------|
| Sep 2023 | Company founded |
| Sep 2024 | rerank-2 and rerank-2-lite released |
| Oct 2024 | $20M Series A |
| Feb 2025 | MongoDB acquires Voyage AI for ~$220M |
| Aug 2025 | rerank-2.5 with instruction-following, 32K context |
| Oct 2025 | Published "The Case Against LLMs as Rerankers" |
| Jan 2026 | Voyage 4 embedding series (no new reranker models) |

---

## Alternatives

### Commercial APIs

| Provider | Model | Pricing | Context | Key Feature |
|----------|-------|---------|---------|-------------|
| **Voyage AI** | rerank-2.5 | $0.05/M tokens | 32K | Instruction-following |
| **Voyage AI** | rerank-2.5-lite | $0.02/M tokens | 32K | Fast, cheaper |
| **Cohere** | Rerank 3.5 | $2.00/1K searches | 4K | Enterprise, multilingual |
| **Cohere** | Rerank 4 (Dec 2025) | $2.00/1K searches* | 32K | Self-learning, Fast/Pro variants |
| **Jina AI** | reranker-v2 | $0.02/M tokens | — | Code search, 100+ languages |
| **Jina AI** | reranker-v3 | — | — | Late-interaction architecture |
| **ZeroEntropy** | zerank-1 / zerank-2 | — | — | Highest Agentset ELO |

*Cohere Rerank 4 pricing not confirmed; may differ from 3.5.

**Cost comparison (1M requests, 100 docs × 500 tokens each):**
- Voyage rerank-2.5: ~$3,000
- Voyage rerank-2.5-lite: ~$1,200
- Cohere Rerank 3.5: ~$2,000,000 (per-search pricing, dramatically more expensive)
- Jina reranker-v2: comparable to Voyage lite

*Note: Cohere's pricing includes docs up to 500 tokens per search unit. Documents over 500 tokens are split into chunks, each counting separately.*

> [Cohere Pricing](https://cohere.com/pricing) — per-search model
> [Jina Reranker](https://jina.ai/reranker/) — token-based pricing
> [Voyage AI Pricing](https://docs.voyageai.com/docs/pricing) — token-based pricing

### Open-Source / Local Models

| Model | Params | License | BEIR nDCG@10 | Local Feasibility |
|-------|--------|---------|-------------|-------------------|
| **Qwen3-Reranker-0.6B** | 0.6B | Apache 2.0 | — | Good (ONNX available) |
| **Qwen3-Reranker-8B** | 8B | Apache 2.0 | MTEB multilingual #1 (70.58) | Needs beefy machine |
| **gte-reranker-modernbert-base** | 149M | — | 83% Hit@1 | Excellent (small, ONNX, INT8) |
| **jina-reranker-v3** | 0.6B | CC-BY-NC 4.0 | 61.94 | Good (<200ms) |
| **mxbai-rerank-large-v2** | 1.5B | Apache 2.0 | 57.49 | Tight on laptop |
| **mxbai-rerank-base-v2** | 0.5B | Apache 2.0 | 55.57 | Good |
| **BGE-reranker-v2-m3** | 568M | MIT | — | Feasible (fp16) |
| **cross-encoder/ms-marco-MiniLM-L6-v2** | ~33M | — | — | Trivial |
| **FlashRank** | ~4MB | — | — | Trivial (CPU, no PyTorch) |

> [Qwen blog](https://qwenlm.github.io/blog/qwen3-embedding/) — Qwen3 reranker family
> [Mixedbread blog](https://www.mixedbread.com/blog/mxbai-rerank-v2) — mxbai-rerank-v2
> [HuggingFace](https://huggingface.co/Alibaba-NLP/gte-reranker-modernbert-base) — gte-reranker-modernbert
> [FlashRank GitHub](https://github.com/PrithivirajDamodaran/FlashRank) — lightweight reranking

**License caution:** Jina models are CC-BY-NC 4.0 — commercial use requires API or marketplace purchase.

### LLM-Based Reranking

Using LLMs directly as rerankers (RankGPT, RankLLM, AFR-Rank) via listwise prompting. The query and candidate list go into the LLM, which outputs a ranking.

**Against (from Voyage AI's "The Case Against LLMs as Rerankers", Oct 2025):**
- Purpose-built rerankers are **60x cheaper** ($0.05/M tokens vs $1.25-3.00/M)
- **48x faster**
- **15% more accurate** than LLM-based reranking
- Deterministic, reproducible

**For:**
- Higher quality ceiling with GPT-4-class models on some benchmarks
- Better at nuanced, subjective relevance judgments
- No training required — zero-shot

> [Voyage AI Blog](https://blog.voyageai.com/2025/10/22/the-case-against-llms-as-rerankers/) — empirical comparison
> [RankLLM SIGIR 2025](http://zijianchen.ca/publications/rankllm_SIGIR2025.pdf) — unified LLM reranking

---

## Code Search Reranking

### Evidence That Reranking Helps for Code

**TOSS (Two-Stage Code Search):** Tested on CodeSearchNet across 6 languages. BM25/bi-encoder first stage → cross-encoder reranking. Result: MRR of 0.763, a **7.1% gain** over best baseline (GraphCodeBERT at 0.713).
> [Revisiting Code Search in a Two-Stage Paradigm](https://arxiv.org/html/2208.11274)

**CoRNStack (ICLR 2025):** 21M contrastive examples for code. First work to finetune LLMs as code rerankers. A 7B code reranker "considerably improves performance over the retriever" with "significant improvements in function localization for GitHub issues." Code-specific reranking is still largely underexplored.
> [CoRNStack](https://arxiv.org/abs/2412.01007) — code retrieval and reranking

**Voyage AI** evaluates code as one of 9 benchmark domains. rerank-2.5 shows strong code performance. With instruction-following, code relevance can be steered (e.g., "prioritize exact function matches over documentation").

### Code-Specific Models

No dedicated code reranker model exists from any major provider. Voyage offers **voyage-code-3** for code embeddings (first-stage retrieval), which pairs with rerank-2.5 for the second stage. Jina reranker-v2 explicitly supports code search.

> [Voyage AI Blog](https://blog.voyageai.com/2024/12/04/voyage-code-3/) — code embeddings

---

## Performance Characteristics

### Latency

| Approach | Latency | Notes |
|----------|---------|-------|
| Commercial APIs (Voyage, Cohere) | ~595-603ms | Network + inference |
| ZeroEntropy zerank-1 | p50: 130ms | Fastest commercial |
| jina-reranker-v3 (local) | <200ms | 0.6B params |
| Cross-encoder on GPU | 50-100ms | BGE-reranker-v2-m3 |
| FlashRank (CPU) | Very fast | ~4MB model |
| LLM-based (naive) | ~5 seconds | |
| LLM-based (optimized) | <1 second | Prompt caching, parallelization |

**Scaling with document length:** 100 docs × 256 tokens = ~150ms. 100 docs × 4096 tokens = ~7 seconds. Document length matters enormously.

> [Agentset](https://agentset.ai/rerankers) — API latency comparison
> [ZeroEntropy](https://www.zeroentropy.dev/articles/lightning-fast-reranking-with-zerank-1) — zerank latency
> [ZeroEntropy Guide](https://www.zeroentropy.dev/articles/ultimate-guide-to-choosing-the-best-reranking-model-in-2025) — scaling behavior

### Cost Economics

**API rerankers:** $0.02-0.05/M tokens (Voyage) vs $2.00/1K searches (Cohere). At scale, Voyage's token-based pricing is dramatically cheaper.

**LLMs as rerankers:** $1.25-3.00/M tokens — 25-60x more expensive than purpose-built rerankers.

**Local models:** Free after compute. ONNX + INT8 quantization makes small models (gte-modernbert at 149M, ms-marco-MiniLM at 33M) viable on any laptop.

**ROI insight:** Rerankers filter results before sending to a large LLM, saving expensive LLM tokens. The reranker cost is typically dwarfed by the LLM generation cost it reduces.

> [Voyage AI Blog](https://blog.voyageai.com/2025/10/22/the-case-against-llms-as-rerankers/) — cost comparison

### Hybrid Architecture Patterns

RRF (Reciprocal Rank Fusion) and cross-encoder reranking are **complementary, not competing**:

1. Stage 1: Hybrid search (vector + BM25) merged with RRF (~100 candidates, high recall)
2. Stage 2: Cross-encoder reranking of merged candidates (top-k, high precision)

RRF is cheap and unsupervised but lacks semantic understanding. Cross-encoders are expensive but produce query-aware relevance scores. The combined approach leverages both.

> [Progress blog](https://www.progress.com/blogs/master-advanced-search-ranking-fusion-and-reranking-explained) — fusion + reranking
> [Assembled blog](https://www.assembled.com/blog/better-rag-results-with-reciprocal-rank-fusion-and-hybrid-search) — RRF in RAG

---

## .NET / ONNX Considerations

No turnkey .NET reranker library exists. The integration path:

1. Export cross-encoder model to ONNX (via Sentence Transformers or Optimum)
2. Load via `Microsoft.ML.OnnxRuntime` NuGet package
3. Tokenize input (query + document concatenated)
4. Run inference, extract relevance score

Relevant precedents:
- RepoQL already uses ONNX for embeddings (`src/RepoQL.Embeddings/`)
- `yuniko-software/bge-m3-onnx` demonstrates C# ONNX embedding pattern
- Microsoft's [BERT NLP C# tutorial](https://onnxruntime.ai/docs/tutorials/csharp/bert-nlp-csharp-console-app.html) covers the inference pattern
- INT8 quantization yields 2.7-3.4x speedup while retaining 94-98% quality

For API-based reranking: simple HTTP client suffices. No SDK needed.

---

## Comparison

| Dimension | Voyage AI rerank-2.5 | Cohere Rerank 3.5 | Jina reranker-v3 | Local (gte-modernbert) |
|-----------|---------------------|-------------------|-------------------|----------------------|
| Quality (vendor claims) | Best on 93-dataset suite | SOTA on BEIR (claimed) | 61.94 BEIR nDCG@10 | 83% Hit@1 |
| Context window | 32K | 4K | — | — |
| Pricing | $0.05/M tokens | $2.00/1K searches | $0.02/M tokens | Free |
| Instruction-following | Yes (unique) | No | No | No |
| Code search | General + steering | General | Explicit support | General |
| Local deployment | No (API only) | No (API only) | Open weights (CC-BY-NC) | Yes (ONNX) |
| .NET integration | HTTP client | HTTP client | HTTP client or ONNX | ONNX Runtime |
| Latency | ~600ms | ~600ms | <200ms (local) | Depends on hardware |
| Multilingual | 31+ languages | 100+ languages | 100+ languages | — |

---

## Recent Papers and Advances

| Paper/Release | Date | Key Finding |
|--------------|------|-------------|
| CoRNStack (ICLR 2025) | 2025 | First code-specific reranker training dataset. 7B code reranker significantly improves function localization. |
| Jina-reranker-v3 | Sep 2025 | "Last but not late" interaction — 0.6B params matching much larger models at BEIR 61.94. |
| Voyage "Case Against LLMs as Rerankers" | Oct 2025 | Purpose-built rerankers 60x cheaper, 48x faster, 15% more accurate than LLMs. |
| Cohere Rerank 4 | Dec 2025 | Self-learning (adapts without annotated data), 32K context, Fast/Pro variants. |
| EMNLP Findings 2025 | 2025 | Evaluated 22 LLM reranking approaches; lightweight models offer comparable efficiency on novel queries. |
| Evolution of Reranking Models (survey) | Dec 2025 | Comprehensive survey from heuristic methods to LLMs. |
| RankLLM (SIGIR 2025) | 2025 | Unified Python package for LLM-based reranking methods. |

> [CoRNStack](https://arxiv.org/abs/2412.01007), [jina-reranker-v3](https://arxiv.org/abs/2509.25085), [Voyage blog](https://blog.voyageai.com/2025/10/22/the-case-against-llms-as-rerankers/), [Cohere Rerank 4](https://cohere.com/blog/rerank-4), [EMNLP Findings](https://aclanthology.org/2025.findings-emnlp.305/), [Survey](https://arxiv.org/html/2512.16236v1), [RankLLM](http://zijianchen.ca/publications/rankllm_SIGIR2025.pdf)

---

## Gaps

- **Head-to-head benchmarks on identical datasets** across Voyage, Cohere, Jina, and open-source models are scarce. Each vendor publishes favorable self-benchmarks. Agentset is the closest to independent but uses LLM-as-judge methodology.
- **Code-specific reranker performance** — no vendor publishes isolated code-domain NDCG numbers. Voyage shows percentage improvements across 9 domains including code, but per-domain raw scores aren't available.
- **Local model CPU latency on developer laptops** — no published per-query millisecond numbers for gte-modernbert or Qwen3-0.6B on typical hardware.
- **Cohere Rerank 4 pricing** — announced Dec 2025 with new capabilities, but current per-search cost not confirmed to differ from 3.5.
- **ZeroEntropy zerank pricing** — tops the Agentset leaderboard but pricing not publicly available.
- **Jina reranker-v3 pricing** — changed May 2025, current rates unclear in public sources.
- **No .NET reranker libraries exist** — would need to build ONNX wrapper or use HTTP client for APIs.
- **Cohere's $2/1K pricing vs token-based** — the actual cost comparison depends heavily on document length and count per query. Short documents favor Cohere; long documents or many docs per query strongly favor token-based pricing (Voyage, Jina).

---

## Summary

### Providers

| Provider | Best Model | Pricing Model | Differentiator | Acquired By |
|----------|-----------|---------------|----------------|-------------|
| Voyage AI | rerank-2.5 | $0.05/M tokens | Instruction-following, 32K context | MongoDB ($220M, Feb 2025) |
| Cohere | Rerank 4 | $2.00/1K searches | Self-learning, enterprise | — |
| Jina AI | reranker-v3 | $0.02/M tokens | Late interaction, code search, open weights | — |
| ZeroEntropy | zerank-2 | — | Highest independent quality | — |
| Mixedbread | mxbai-rerank-large-v2 | Free (Apache 2.0) | Open, 57.49 BEIR | — |
| BAAI | bge-reranker-v2-m3 | Free (MIT) | Open, multilingual | — |
| Qwen | Qwen3-Reranker-0.6B/8B | Free (Apache 2.0) | Family spanning 0.6B-8B, 100+ languages | — |
| Alibaba | gte-reranker-modernbert | Free | 149M params, ONNX-friendly | — |

### Approaches

| Approach | Quality | Latency | Cost | Local? |
|----------|---------|---------|------|--------|
| Purpose-built cross-encoder (API) | High | ~600ms | Low-medium | No |
| Purpose-built cross-encoder (local ONNX) | Medium-high | 50-200ms | Free | Yes |
| LLM-based (RankGPT, listwise) | Highest ceiling | Seconds | High (25-60x more) | Depends on LLM |
| Late interaction (ColBERT) | Medium-high | Very fast | Free (local) | Yes |
| Lightweight (FlashRank, MiniLM) | Medium | Very fast | Free | Yes |
