# LLM Service Flows

Flows for a gRPC web service that provides LLM synthesis and cloud embedding/reranking to RepoQL hosts. Local ONNX handles volume (JIT embeddings, interactive search). The service handles insight (explain, reranking, optional batch embedding).

## North Star

See [north-star](../../../north-star/llm-service.md) — every cloud token must return more value than it costs.

## Flows

| Flow | Trigger | Cloud cost |
|------|---------|------------|
| [auth-and-billing](auth-and-billing.md) | User activates via GitHub Marketplace or Stripe | None (billing infrastructure) |
| [explain](explain.md) | Agent calls `explain` tool | Grok 4.1 Fast: ~$0.001-0.01 per question |
| [reranking](reranking.md) | Explore retrieval exceeds candidate threshold | Voyage rerank-2.5: ~$0.0001 per rerank |
| [batch-embedding](batch-embedding.md) | Paid account with cloud embedding enabled | Voyage voyage-context-3: ~$0.06/1M tokens, symbol-level chunks |
| [service-lifecycle](service-lifecycle.md) | Service starts, providers checked | None |
| [failure-modes](failure-modes.md) | Provider failures, network issues | None |

## Architecture Context

```
Agent ─── MCP Client ──── RepoQL Host ──── LLM Service ──── Grok 4.1 Fast
                              │                  │
                              │                  └────────── Voyage AI
                              │
                              └── Local ONNX (JIT embed, structural search)
```

The RepoQL host already has `ILlmProvider` and `IEmbeddingProvider` abstractions. The LLM service replaces `OpenRouterLlmProvider` with a gRPC-backed implementation that calls the shared service. The local ONNX `IEmbeddingProvider` remains unchanged — it continues to handle JIT embedding and interactive search.

**Cloud embedding approach:** Voyage contextualized chunk embeddings (`voyage-context-3`). Instead of one embedding per file, each symbol (method, class, section) gets its own embedding — encoded with awareness of its sibling symbols. Search goes from file-level to symbol-level precision.
