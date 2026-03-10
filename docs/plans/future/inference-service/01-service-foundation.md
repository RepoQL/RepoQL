---
description: Plan for the inference service foundation — proto, Grok client, unary RPC, auth, deploy
tags: [plan, inference, grpc, cloud]
audience: { human: 60, agent: 40 }
purpose: { plan: 100 }
---

# Plan: Inference Service Foundation

Implements: [Inference Service Design](../../../designs/future/inference-service.md) — proto definition, Cloud Run service project, Grok gRPC Chat client, unary `Complete` RPC, auth interceptor, deploy workflow.

## Scope

**Covers:**
- Proto file (`inference.proto`) with full message/service definitions from the design
- `RepoQL.Inference.Proto` project — proto, generated code
- `RepoQL.Inference.Service` project — Cloud Run service (ASP.NET gRPC, following embedding service conventions)
- `GrokClient` — xAI gRPC Chat API client for text completion
- Effort → model mapping (server-side configuration)
- Unary `Complete` RPC implementation (no tools, no streaming)
- Auth interceptor extended for both unary and bidi streaming
- GitHub Actions deploy workflow (`deploy-inference.yml`)
- Smoke test documentation
- Tests for all components

**Does not cover:**
- Bidirectional streaming `CompleteWithTools` (Plan: 02-tool-loop)
- Tool relay, budget enforcement, round tracking (Plan: 02-tool-loop)
- Host-side client, explain replacement, fallback (Plan: 03-host-integration)
- Pulumi infrastructure (reuse embedding service patterns, extend manually for now)

## Enables

Once the foundation exists:
- **Simple completion works end-to-end** — the host (or any gRPC client) can send a prompt and get a response from Grok, with effort-based model selection
- **Plan: 02-tool-loop** can proceed — the bidi streaming path builds on the service, auth, and Grok client this plan creates
- **Plan: 03-host-integration** can proceed — the host-side client connects to the service this plan deploys
- **Auth is complete** — both unary and bidi paths are authenticated, so Plan 02 doesn't need to deal with auth

## Prerequisites

- GCP project with Artifact Registry repo (exists — reuse `repoql-dev`/`repoql-production`)
- WIF service account with Cloud Run deploy permissions (exists — `github-actions-sa`)
- Secret Manager secret for xAI API key (new — `repoql-inference-grok-api-key`)
- Secret Manager secret for client auth key hash (new — `repoql-inference-auth-key-hash-0`)
- Familiarity with [Grok 4.1 Fast API](../../../research/grok-4-1-fast.md) — gRPC Chat API, `developer` role, function calling

## North Star

A RepoQL host sends a prompt and effort level. The server picks the right Grok model, calls it via gRPC, and returns the response. The client never knows or cares which model answered. Adding a new provider requires zero client changes. Auth works identically to the embedding service.

## Done Criteria

### Proto

- The proto file shall define all messages and services from the [design](../../../designs/future/inference-service.md#the-grpc-api)
  - `InferenceService` with `Complete` and `CompleteWithTools` RPCs
  - `CompleteRequest`, `ClientMessage`, `ServerMessage`, `ToolRequest`, `ToolResponse`, `ToolDefinition`, `Completion`, `Effort`, `StopReason`, `Usage`
- The `RepoQL.Inference.Proto` project shall generate C# code from the proto
  - When the proto changes, `dotnet build` shall regenerate without manual steps

### Grok Client

- The `GrokClient` shall call xAI's gRPC Chat API (`api.x.ai:443`) to generate text completions
  - Generated from published protos at [`xai-org/xai-proto`](https://github.com/xai-org/xai-proto) — `proto/xai/api/v1/chat.proto`
  - Key RPCs: `GetCompletion` (unary), `GetCompletionChunk` (streaming for tool loop)
  - The request shall use the `developer` role for system prompts (not `instructions`)
  - When tool definitions are provided, they shall be included as function tools
  - Auth: `Authorization: Bearer <API key>` as gRPC metadata
  - `Completion.model` shall contain the Grok model ID actually used (e.g., `grok-4-1-fast-reasoning`)
- The `GrokClient` shall map `Effort` to Grok model and parameters
  - `EFFORT_LOW` → `grok-4-1-fast-non-reasoning` (lower temperature)
  - `EFFORT_BALANCED` → `grok-4-1-fast-non-reasoning` (moderate temperature)
  - `EFFORT_HIGH` → `grok-4-1-fast-reasoning` (thinking tokens enabled)
  - `EFFORT_UNSPECIFIED` → same as `EFFORT_BALANCED`
- The `GrokClient` shall extract reasoning tokens from the response when available
  - When the model produces thinking tokens, they shall be returned separately from content
- The `GrokClient` shall report token usage (input, output, thinking)
- When the xAI API returns an error, the `GrokClient` shall throw with the error details
- When the xAI API times out, the `GrokClient` shall throw a timeout exception

### Unary Complete RPC

- When a client calls `Complete`, the service shall return a `Completion` with content, reasoning (if any), stop_reason, usage, and model
- The service shall assemble the LLM context in order: developer message → context → user message
  - When `context` is empty, it shall be omitted
  - When `system` is empty, a default system prompt shall be used
- When `max_tokens` is provided, it shall be passed to the Grok API as guidance
- When the Grok API is unreachable, the service shall return gRPC `UNAVAILABLE`
- When the Grok API returns an error, the service shall return gRPC `INTERNAL` with logged details
- The `tools`, `tool_token_budget`, and `max_rounds` fields shall be ignored on the unary path

### Auth

- The service shall authenticate requests via Bearer token → SHA-256 hash comparison
  - Same pattern as the embedding service: extract from `Authorization` metadata, hash, compare against stored hashes
- The auth interceptor shall handle both `UnaryServerHandler` and `DuplexStreamingServerHandler`
  - When the token is invalid, return gRPC `UNAUTHENTICATED` on both paths
  - When the `Authorization` header is missing, return gRPC `UNAUTHENTICATED`
- Auth key hashes shall be loaded from Secret Manager (mounted as environment variables by Cloud Run)

### Deploy

- The `deploy-inference.yml` workflow shall build and deploy the service to Cloud Run
  - Manual dispatch with `environment` parameter (dev/prod)
  - `dotnet publish /t:PublishContainer` → push to Artifact Registry → `gcloud run deploy`
  - `<PublishTrimmed>false</PublishTrimmed>` (same .NET 10 gotcha as embedding service)
- The deployed service shall scale to zero when idle
- gRPC Chat API is stateless by default — no special configuration needed for no-retention

### Observability

- The service shall emit OTel traces for each RPC call
  - Span per `Complete` call with effort, model, and token usage as attributes
  - Span per Grok API call with model and response time
- The service shall log at `Info` level: request received, Grok model selected, completion returned

### Tests

- Unit tests for `GrokClient` with mocked gRPC responses (Chat API proto format)
- Unit tests for Effort → model mapping
- Unit tests for auth interceptor (valid token, invalid token, missing header) on both unary and streaming paths
- Integration test: `Complete` RPC with mocked Grok backend → verify response structure

## Constraints

- **Project structure** — follow embedding service conventions (`RepoQL.Inference.Proto`, `RepoQL.Inference.Service`). See [embedding.proto](../../../src/RepoQL.Embedding.Proto/Protos/embedding.proto) and the embedding service project for patterns.
- **No trimming** — `<PublishTrimmed>false</PublishTrimmed>` (design constraint from .NET 10 + Google.Apis reflection, documented in [ops guide](../../../designs/current/cloud-embedding-cache-ops.md#architecture-decisions))
- **Auth reuse** — extend the existing `ApiKeyAuthInterceptor` pattern, don't create a new one. The embedding service's implementation is the reference.
- **No Pulumi yet** — infrastructure (secrets, service account, IAM) created manually for now. Pulumi extension is follow-on work.
- **Grok gRPC Chat API** — not REST Responses API, not Chat Completions. Generate C# client from published protos. See [research notes](../../../research/grok-4-1-fast.md#api-surfaces).

## References

- [Design: Inference Service](../../../designs/future/inference-service.md) — proto, trade-offs, effort mapping
- [North Star: Inference Service](../../../north-star/inference-service.md) — what great looks like
- [Flows: Simple Completion](../../../flows/future/inference-service/simple-completion.md) — the unary flow this plan implements
- [Grok 4.1 Fast Research](../../../research/grok-4-1-fast.md) — API surface, pricing, gotchas
- [Embedding Service](../../../designs/current/cloud-embedding-cache.md) — sibling service, reference for project structure
- [Ops Guide](../../../designs/current/cloud-embedding-cache-ops.md) — auth, secrets, deploy patterns to reuse
- [Testing Guidelines](../../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy
- [xai-proto](https://github.com/xai-org/xai-proto) — published proto files for gRPC client generation

## Error Policy

Errors should be mapped to appropriate gRPC status codes with descriptive messages. The Grok client should throw typed exceptions that the RPC handler maps to status codes. Auth failures are `UNAUTHENTICATED`. Validation failures are `INVALID_ARGUMENT`. Grok API errors are `INTERNAL` (logged) or `UNAVAILABLE` (retriable). Never swallow errors silently.
