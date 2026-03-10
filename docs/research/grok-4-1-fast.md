# Grok 4.1 Fast API Reference

Research notes for the inference service design. Source: xAI docs + launch post, March 2026.

## Models

| Model ID | Style | Use case |
|----------|-------|----------|
| `grok-4-1-fast-reasoning` | Thinking tokens, multi-step planning | Agentic, ambiguous tool use, deep research |
| `grok-4-1-fast-non-reasoning` | Instant responses, no reasoning overhead | UI-facing, extraction, deterministic tool paths |

Both: 2M token context window, 4M TPM, 480 RPM.

Older `grok-4-fast-*` family still listed — names are easy to confuse.

Pin dated model versions (`<model>-<date>`) for benchmarking. Floating aliases (`-latest`) move automatically.

## Pricing

| Token type | Rate |
|------------|------|
| Input | $0.20/M |
| Cached input | $0.05/M (75% discount) |
| Output (including reasoning) | $0.50/M |

Server-side tools have per-invocation pricing (Web Search $5/1k, etc.) — not relevant for client-side function calling.

**Hidden cost in agentic flows:** not the final answer text. `prompt_tokens` accumulate across the internal loop, `reasoning_tokens` cover planning. Long tool loops + growing context dominate spend.

## API Surfaces

xAI offers three interfaces. For the inference service, **gRPC Chat API** is preferred — we're already a gRPC service, function calling is supported, and we don't need stateful continuation.

### gRPC Chat API (preferred for inference service)

- Public endpoint: `api.x.ai:443` (TLS)
- Published protos: [`xai-org/xai-proto`](https://github.com/xai-org/xai-proto) — `proto/xai/api/v1/chat.proto`
- Key RPCs: `GetCompletion` (unary), `GetCompletionChunk` (streaming)
- Full function calling support (custom functions)
- Auth: `Authorization: Bearer <API key>` metadata
- **Limitation**: `code_interpreter` and `file_search` tools not supported on gRPC (irrelevant — we only use function calling)
- No official .NET SDK — generate C# client from published protos
- Community example: [`fpinsight/xai-grpc-client`](https://github.com/fpinsight/xai-grpc-client) (Rust, 100% RPC coverage)

### REST Responses API

- `POST /v1/responses` — stateful continuation via `previous_response_id`
- `store: false` to opt out of 30-day storage
- Typed output items: `function_call`, `web_search_call`, `code_interpreter_call`, etc.
- `max_turns` limits assistant/server-side turns within one request
- All tool types supported (superset of gRPC)

### REST Chat Completions

- OpenAI-compatible `POST /v1/chat/completions`
- Legacy, xAI recommends migrating to Responses API or gRPC

### Common

- `instructions` field NOT supported — use `developer` role (alias for `system`), single message, placed first
- Python SDK (`xai-sdk`) uses gRPC internally

## Prompt Caching

Automatic, prefix-based. To maximize hits:
- Keep system/developer message stable
- Keep tool schemas stable
- Don't rewrite earlier history
- Use `x-grok-conv-id` header for related requests

## Tool Calling

- Up to 200 tools per request
- `parallel_tool_calls` enabled by default — disable only for stateful/ordering-dependent tools
- `tool_choice`: `auto` (open-ended), `required` (must use a tool), `none` (no tools), or force a specific function
- In streaming, function calls arrive **whole in a single chunk** (not dribbled)
- `max_turns` only counts assistant/server-side turns. Client-side calls pause and don't consume turns
- Structured outputs supported with tools (schema compliance guaranteed)

## Reasoning Continuation

For multi-turn reasoning workflows, use `reasoning.encrypted_content`:
- Include `["reasoning.encrypted_content"]` in the request
- Carries thinking state across requests without exposing plaintext reasoning
- More portable than relying on `previous_response_id` server state

## Key Gotchas

- Responses API is stateful by default (30-day storage). Set `store: false` for privacy.
- `developer` role = `system` role. Use one, placed first.
- No realtime data without search tools enabled.
- Still billed for full conversation history even with `previous_response_id` continuation.
- Billing uses `server_side_tool_usage` (successful only), not `tool_calls` (all attempts).

## Sources

- [Launch post](https://x.ai/news/grok-4-1-fast)
- [Generate Text](https://docs.x.ai/developers/model-capabilities/text/generate-text)
- [Pricing/llms.txt](https://docs.x.ai/llms.txt)
- [Tool Usage Details](https://docs.x.ai/developers/tools/tool-usage-details)
- [Function Calling](https://docs.x.ai/developers/tools/function-calling)
- [Streaming](https://docs.x.ai/developers/tools/streaming-and-sync)
- [Structured Outputs](https://docs.x.ai/developers/model-capabilities/text/structured-outputs)
- [Reasoning](https://docs.x.ai/developers/model-capabilities/text/reasoning)
- [gRPC API Reference](https://docs.x.ai/developers/grpc-api-reference)
- [xai-proto](https://github.com/xai-org/xai-proto) — published proto files
- [REST API Reference](https://docs.x.ai/docs/api-reference)
