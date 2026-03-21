---
description: Plan for host-side inference client — read tool forwarding, explain replacement, OpenRouter removal
tags: [plan, inference, client, host, integration]
audience: { human: 60, agent: 40 }
purpose: { plan: 100 }
status: ready — Plans 01+02 complete. IInferenceProvider design complete.
---

# Plan: Host Integration

Implements: [Inference Service Design](../../../designs/future/inference-service.md) and [IInferenceProvider Design](../../../designs/future/inference-provider-refactor.md) — host-side gRPC client, `ILlmProvider` → `IInferenceProvider` refactor, read tool definition forwarding, `explain` tool replacement, OpenRouter removal.

> **Status:** Ready. Plans 01 and 02 are complete (inference service operational with both unary and bidi streaming RPCs). The `IInferenceProvider` interface design is complete — see [inference-provider-refactor.md](../../../designs/future/inference-provider-refactor.md) for the interface, types, consumer migrations, and deletion list.

## Scope

**Covers:**
- `InferenceClient` — gRPC client for the inference service (unary + bidi streaming)
- Inference-safe read tool definition (strip `=> question:` modifier from description before forwarding)
- Tool execution loop — receive `ToolRequest`, execute `read` against local graph, return `ToolResponse` with `tokens_used`
- `explain` replacement — wire the inference service as the LLM backend for explain-style queries
- Keyword extraction — server-side unary `Complete` call to extract keywords before the main tool-use flow
- OpenRouter removal — the inference service replaces `OpenRouterLlmProvider` entirely, no fallback
- Configuration — service URL, API key, enable/disable
- Tests for client, tool execution, degradation

**Does not cover:**
- Proto definition (Plan: 01-service-foundation)
- Server-side implementation (Plans: 01 and 02)
- Deploy (Plan: 01-service-foundation)
- `ILlmProvider` refactor design (done — see [inference-provider-refactor.md](../../../designs/future/inference-provider-refactor.md))
- New tools beyond `read` (design constraint — read only for now)

## Enables

Once host integration exists:
- **`explain` uses Grok instead of OpenRouter** — deeper reasoning, tool-assisted follow-up reads, same interface to the agent
- **Agents get the full tool-assisted answer flow** — explore locally → send context → LLM reads follow-up files → synthesized response with citations
- **OpenRouter removed** — single LLM provider, simpler architecture
- **Effort-based model selection** — the host conveys intent, not model names. Future provider changes are invisible

## Prerequisites

- Plans 01 and 02 complete — inference service deployed and operational with both unary and bidi streaming RPCs ✓
- [`IInferenceProvider` design complete](../../../designs/future/inference-provider-refactor.md) — interface, types, consumer migrations, deletion list all specified ✓
- Existing `read` tool implementation in the host (already exists)
- Host's tokenizer for approximate `tokens_used` counting (already exists — used by budget allocation)
- `RepoQlConfig` settings infrastructure with `[Setting]` attributes (already exists)

## North Star

The host calls `explain` and gets a better answer than before — backed by Grok with follow-up reads instead of a single OpenRouter call. The agent never knows the LLM changed. If the cloud service is down, LLM features degrade gracefully — no crashes, clear messaging, the rest of RepoQL works fine.

## Done Criteria

### InferenceClient

- The `InferenceClient` shall connect to the inference service via gRPC
  - The service URL shall be configurable via `RepoQlConfig`
  - The API key shall be configurable via `RepoQlConfig`
- When calling `Complete` (unary), the client shall send a `CompleteRequest` and return the `Completion`
- When calling `CompleteWithTools` (bidi), the client shall:
  - Send the `CompleteRequest` as the first stream message
  - Receive `ToolRequest` messages and execute them locally
  - Send `ToolResponse` messages with results and `tokens_used`
  - Return the final `Completion` when received
- When the service is unreachable, the client shall throw a typed exception (not a raw gRPC error)

### Read Tool Forwarding

- The client shall build an inference-safe `read` tool definition
  - Same name and parameters as the MCP/CLI definition
  - Description shall have the `=> question:` modifier documentation stripped (prevents LLM-triggering-LLM outside budget tracking)
  - All other modifiers (`tree`, `history`, `blame`, `lint`, content) are included
- When the host's `read` tool definition changes, the inference-safe variant shall update automatically
  - The stripping is applied at forwarding time, not hardcoded

### Tool Execution

- When the client receives a `ToolRequest` for `read`, it shall execute the read tool against the local RepoQL graph
  - Arguments from `arguments_json` shall be deserialized and passed to the read implementation
  - If the arguments contain `=> question:` modifier, the client shall reject the call with `is_error = true`
  - The result shall be the same content the MCP/CLI consumer would receive (minus question modifier)
- The client shall count tokens in the result using the host's tokenizer
  - `tokens_used` in the `ToolResponse` shall reflect an approximate token count (Claude tokenizer, not Grok's — acceptable tolerance)
- When `read` execution fails, the `ToolResponse` shall have `is_error = true` with the error message as content
- When the client receives parallel `ToolRequest` messages (`more_in_round = true`), it shall execute them concurrently

### Explain Replacement

- The host shall use the inference service for explain-style queries
  - Keyword extraction: unary `Complete` call with `EFFORT_LOW` to extract search keywords from the user's question
  - Context gathering: local `explore` with extracted keywords (same as current explain's internal explore)
  - Main query: `CompleteWithTools` with `context` = explore results, `effort` = `EFFORT_HIGH`, read tool
  - `tool_token_budget` = configurable, default 30000
  - `max_rounds` = configurable, default 5
- The response shall include the LLM's content (the answer) and reasoning (logged, not shown to agent)
- The `explain` tool's interface to the agent shall not change — same parameters, same response format

### No Fallback

- OpenRouter shall be removed as a dependency — the inference service is the only LLM provider
- When the inference service is unreachable, LLM-powered features shall degrade gracefully
  - `explain` shall return an error indicating the inference service is unavailable
  - All non-LLM features (explore, read, query, import) shall continue working normally
  - The error message shall be actionable ("inference service unreachable at {url}")
- When the inference service is not configured, LLM-powered features shall be disabled
  - No connection attempt, no error — features simply unavailable

### Configuration

- The following settings shall be configurable via `RepoQlConfig`:
  - `Inference:ServiceUrl` — gRPC endpoint (e.g., `https://repoql-inference-HASH.us-central1.run.app`)
  - `Inference:ApiKey` — Bearer token for authentication
  - `Inference:Enabled` — boolean, default `false` (opt-in)
  - `Inference:ToolTokenBudget` — default tool token budget for explain, default 30000
  - `Inference:MaxRounds` — default max rounds for explain, default 5
- When `Inference:Enabled` is false, no gRPC connection shall be established

### Tests

- Unit tests for `InferenceClient`: unary call, bidi call with mock tool requests, error handling
- Unit tests for inference-safe read definition: `=> question:` stripped, other modifiers preserved
- Unit tests for tool execution: successful read, failed read (is_error), question modifier rejected, parallel execution
- Unit tests for degradation: service unreachable → graceful error, service not configured → features disabled
- Integration test: mock inference service → client sends request with read tool → executes locally → returns completion

## Constraints

- **`IInferenceProvider` replaces `ILlmProvider`** — follow the [design](../../../designs/future/inference-provider-refactor.md) exactly. The agent-facing `explain` tool interface must not change.
- **Read tool only, no question modifier** — the forwarded `read` definition strips `=> question:`. Server-side enforcement is policy (not enforced by server); client-side rejection is the safety net.
- **Tokenizer is approximate** — `tokens_used` uses a Claude tokenizer, not Grok's. Small discrepancies are acceptable. Budget is directional, not byte-exact.
- **No fallback** — the inference service replaces OpenRouter entirely. When the service is down, LLM features are unavailable. This is a deliberate simplification — one provider, not two.

## References

- [Design: Inference Service](../../../designs/future/inference-service.md) — client usage examples, trade-offs
- [Design: IInferenceProvider](../../../designs/future/inference-provider-refactor.md) — interface, types, consumer migrations, deletion list
- [Flow: Tool-Use Completion](../../../flows/future/inference-service/tool-use-completion.md) — client's role in the flow (stages 5, 6)
- [Flow: Simple Completion](../../../flows/future/inference-service/simple-completion.md) — unary client usage
- [North Star: Inference Service](../../../north-star/inference-service.md) — "The Right Split" and "Failure as Guidance"
- `src/RepoQL.LLM.Client/OpenRouterLlmProvider.cs` — to be replaced
- `src/RepoQL.Read/` — read tool implementation
- `src/RepoQL.Explore/` — explore implementation (used to gather context before calling inference)
- [Testing Guidelines](../../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

The inference service is the only LLM provider — not a best-effort accelerator. When it's down, LLM features are unavailable. Errors should propagate clearly to the caller with actionable messages. Non-LLM features must never be affected by inference service availability. Configuration errors (missing API key when enabled) should fail loudly at startup.
