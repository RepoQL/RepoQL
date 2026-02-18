---
description: North star for a gRPC LLM provider service fronting Voyage AI and Grok 4.1 Fast
tags: [north-star, llm, embedding, grpc, voyage, grok, explain, search]
audience: { human: 55, agent: 45 }
purpose: { north-star: 100 }
---

# LLM Service: What Great Looks Like

> Every cloud token should return more value than it costs. Local handles the volume; cloud handles the insight.

An agent explores a codebase. Structural queries, JIT embeddings, semantic search — all local, all instant, all free. The local model handles the volume: thousands of passages embedded during indexing, hundreds of similarity comparisons during search. Then the agent asks "how does authentication work?" That question — the one that requires reading 50 files, synthesizing across boundaries, producing a coherent explanation — goes to Grok 4.1 Fast via a shared gRPC service. The synthesis costs a fraction of a cent and saves the agent minutes of file-by-file reading. When initial retrieval returns 40 candidates, Voyage's reranker scores them so the top 10 are genuinely the best — not just the closest vectors from a small local model. The cloud earns its keep on the hard problems. The local model earns its keep on everything else.

---

## Economics First

- An agent should get structural queries, JIT embedding, and basic semantic search for free — powered by the local model, with no cloud calls, no latency penalty, no cost.

- Cloud should only be called when the value clearly exceeds the cost: synthesis from many files (explain), high-stakes reranking of retrieval candidates, and batch embedding where quality materially improves search.

- An operator should be able to see what the service costs — per query type, per time period, per repo — so that ROI is measurable, not assumed.

- The service should make the cost/quality tradeoff explicit: local embedding is the default for indexing and interactive search; cloud reranking and cloud embedding are opt-in upgrades applied where the quality delta justifies the spend.

---

## Cloud for Insight

- An agent should get synthesis from a model with a 2M-token context window, so that an entire codebase's relevant context fits in a single call — not truncated, not summarized prematurely, not split across multiple requests.

- An agent should benefit from cross-encoder reranking after initial retrieval, so that the top results are genuinely the most relevant — not just the closest local vectors.
  ```
  Local retrieval: 40 candidates from ONNX E5-small
  Cloud rerank:    Top 10 re-scored by Voyage rerank-2.5
  Result:          Agent sees the 10 most relevant, not the 10 nearest
  ```

- An agent should be able to trust that explain results reflect a frontier model — without knowing or caring which model that is.

---

## Local for Volume

- The local ONNX model should remain the default for indexing-time embeddings and interactive JIT search — it runs on CPU, costs nothing, and produces results in milliseconds.

- An agent should get useful semantic search from the local model alone. Cloud improves quality at the margins; local provides the baseline that works offline, at scale, for free.

- When the cloud service is unreachable, every feature except explain and reranking should work exactly as it does today — the local model is not a fallback, it is the foundation.

---

## Zero Configuration

- An agent should be able to use explain without setting any API keys or environment variables on the RepoQL host — the host connects to the hosted service, which owns the provider credentials.

- A developer should be able to subscribe at repoql.ai and have explain work immediately — no provider keys, no model downloads, no infrastructure to manage.

- Core features (structural queries, local semantic search, JIT embedding) should work without any account at all — cloud features are additive, not gating.

---

## Independence

- A RepoQL host should be able to start, index, and serve all non-cloud features while the hosted service is unreachable — with explain clearly marked as unavailable, not silently absent.

- The hosted service should be upgradeable independently of RepoQL — a model swap or capacity increase requires zero changes to any RepoQL installation.

- An agent should be able to use cloud features across multiple repositories under a single account — one GitHub identity, one bill, regardless of how many repos.

---

## Budget as Contract

- An agent should be able to request synthesis within a token budget. The service instructs the LLM how many tokens to respond with — enforcement is best-effort, not exact. The LLM may undershoot or overshoot; the service does not truncate.

- An agent should be able to request embeddings for a batch of passages and get back vectors without paying for a round trip per passage — batch efficiency is the service's problem, not the caller's.

---

## Reliability

- An agent should never get a silent failure from explain or semantic search. If the service is down, the agent should see "LLM service unreachable — structural search and local semantic search still available" — not empty results.

- The service should recover from transient provider failures (Voyage rate limits, Grok timeouts) automatically, retrying with backoff — the agent should not see intermittent failures that resolve on retry.

- When a provider is degraded, the host should learn fast — via circuit breakers on real calls, not by discovering it when an explain call hangs for 30 seconds.

- An embedding request that fails partway through a batch should return the successful embeddings and clearly identify which passages failed — not discard the entire batch.

---

## Embedding Quality

- An agent should be able to search at symbol-level granularity — finding the exact method, class, or section, not just the file that contains it.

- Cloud embeddings should use contextualized chunks: each symbol embedded with awareness of its sibling symbols in the same file. A method named `Process` in `JwtMiddleware` should be findable as JWT processing, not generic processing.

- An agent should be able to search across passages embedded by the local model and get correct results — the local embedding space is the primary space, not a temporary one waiting for cloud upgrade.

- Cloud embeddings should use a single fixed dimension (1024) — one space, one index, no configuration. Local (384 dims, file-level) and cloud (1024 dims, symbol-level) are separate, incompatible spaces; query routing picks the right one.

- The caller should not need to know about embedding models, dimensions, contextualization, or asymmetric prefixes. Search works; the quality depends on the account tier.

---

## Provider Abstraction

- The service should be able to swap from Grok 4.1 Fast to a different LLM without any RepoQL host being aware — the gRPC contract is the stable surface, not the provider behind it.

- The service should be able to swap from Voyage AI to a different reranking or embedding provider — callers are insulated.

- An operator should be able to see which providers and models are active, their health, and their cost — through the service's own diagnostics, not by reading provider dashboards.

---

## What Great Looks Like

| Dimension | Great | Acceptable | Unacceptable |
|-----------|-------|------------|--------------|
| **Cost visibility** | Per-query-type cost dashboard | Monthly aggregate | No cost tracking |
| **Local capability** | Full structural + semantic search, no cloud | Structural only without cloud | Nothing works without cloud |
| **Explain latency** | < 2s typical | < 5s | > 10s or timeout without progress |
| **Search quality** | Local retrieval + cloud rerank | Local retrieval only | Cloud call per search |
| **Degradation** | Explain unavailable, everything else works | Generic "LLM unavailable" | Silent empty results |
| **Model upgrades** | Transparent, zero client changes | Requires host restart | Requires host code changes |
| **Multi-tenant** | Shared service, per-repo isolation | One service per host | Embedded in host process |
| **Budget** | Richest answer within budget | Answer within budget | Budget ignored |

---

## Anti-Patterns

| Don't | Do Instead |
|-------|------------|
| Call cloud for every semantic search | Local embeddings for retrieval; cloud reranking for the final cut |
| Require API keys on the RepoQL host | Hosted service owns provider credentials; users authenticate with GitHub |
| Treat local model as a fallback | Local is the foundation; cloud is the upgrade for high-value operations |
| Offer BYO keys as an alternative | One hosted service, one billing relationship — simplicity over optionality |
| Mix embedding spaces silently | Track model version per embedding; migrate or partition |
| Block on cloud for structural queries | Cloud features are additive; core graph and local search always available |
| Return partial synthesis as complete | Stream with clear completion signal; budget overflow requires consent |
| Ignore cost per query | Track and expose; ROI that can't be measured can't be justified |

---

*The best token is the one you didn't spend. The second best is the one that paid for itself.*
