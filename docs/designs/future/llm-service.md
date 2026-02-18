---
description: Architecture of the hosted LLM service that fronts Grok and Voyage for RepoQL hosts
tags: [design, llm, service, grpc, grok, voyage, auth, billing]
audience: { human: 60, agent: 40 }
purpose: { design: 85, flow: 15 }
---

# LLM Service Design

## North Star

A thin relay. The intelligence is in the RepoQL host (what to ask, what context to provide) and in the providers (Grok for synthesis, Voyage for embedding and reranking). The service is plumbing: authenticate, forward, meter, return. The thinner it stays, the fewer things break.

**Informed by:**
- [north-star/llm-service.md](../../north-star/llm-service.md) — economics, provider choices, capability scope
- [flows/future/llm-service/](../../flows/future/llm-service/) — explain, reranking, embedding, auth-and-billing, service-lifecycle, failure-modes
- [designs/future/llm-service-integration.md](llm-service-integration.md) — client-side: how the host consumes the service

## Context

RepoQL hosts run on developer laptops. They handle indexing, querying, and local semantic search without any cloud dependency. Three capabilities require cloud providers: LLM synthesis (Grok), cross-encoder reranking (Voyage), and contextualized chunk embedding (Voyage).

The hosted service sits between hosts and providers. It owns the provider API keys so users don't have to. It authenticates users, checks entitlement, forwards requests, meters usage, and returns results. Hosts connect via gRPC with bearer tokens (refresh-token-issued JWTs or operator-issued API keys — see [llm-service-integration.md](llm-service-integration.md#authentication)).

This design covers the server side: what the service is, how it's built, what runs where.

## Constraints

| Constraint | Source | Impact |
|------------|--------|--------|
| No BYO keys | North star | Service owns all provider credentials |
| Host-agnostic deployment | Not yet committed to a cloud | Docker container, no cloud-specific dependencies in the service itself |
| Cloudflare for DNS | Existing infrastructure | Landing page and OAuth redirects can run on Cloudflare; gRPC service needs a separate host |
| Managed PostgreSQL | Decision (this doc) | Accounts, tokens, usage, entitlements |
| Two payment rails | Auth-and-billing flow | GitHub Marketplace and Stripe, unified entitlement |
| Three identity providers | Auth-and-billing flow | GitHub (primary), Google, Apple |
| Provider privacy constraints | Auth-and-billing flow | Voyage opt-out mandatory; embedding sends chunk text, tool calls can return file content |
| TLS required | Security | All gRPC and HTTP in production over TLS; bearer tokens over plaintext is not acceptable |
| Budget best-effort | North star | Service passes `maxTokens` to Grok; doesn't truncate responses |

## Design

### Runtime

ASP.NET Core in a Docker container. Kestrel hosts both gRPC and HTTP on the same port (HTTP/2 for gRPC, HTTP/1.1 for webhooks and OAuth). The existing codebase is C#, the gRPC tooling is first-class, and shared types with the client are possible.

```
┌─────────────────────────────────────────────────────┐
│                    ASP.NET Core                      │
│                                                      │
│   gRPC Services          HTTP Endpoints              │
│   ├── Ask                ├── /auth/{provider}        │
│   ├── ExtractKeywords    ├── /auth/{provider}/cb     │
│   ├── Rerank             ├── /token/refresh          │
│   └── EmbedContextualized├── /webhooks/stripe        │
│                          └── /webhooks/marketplace   │
│                                                      │
│   Auth Interceptor ──── Provider Clients             │
│   Metering ──────────── PostgreSQL                   │
└─────────────────────────────────────────────────────┘
```

### gRPC Service Definition

Six RPCs matching the three client interfaces in [llm-service-integration.md](llm-service-integration.md):

```protobuf
service LlmService {
  // ILlmProvider.AskAsync (no tools) — unary
  rpc Ask(AskRequest) returns (AskResponse);

  // ILlmProvider.AskAsync (with tools) — bidirectional streaming
  rpc AskWithTools(stream ClientMessage) returns (stream ServerMessage);

  // ILlmProvider.ExtractKeywordsAsync — unary
  rpc ExtractKeywords(KeywordsRequest) returns (KeywordsResponse);

  // IReranker.RerankAsync — unary
  rpc Rerank(RerankRequest) returns (RerankResponse);

  // IChunkEmbeddingProvider.EmbedContextualizedAsync — unary (client paginates large batches)
  rpc EmbedContextualized(EmbedRequest) returns (EmbedResponse);

  // IChunkEmbeddingProvider.EmbedQueryAsync — unary (single query text → single vector)
  rpc EmbedQuery(EmbedQueryRequest) returns (EmbedQueryResponse);
}
```

Two separate RPCs for Ask: unary (no tools, simpler) and bidirectional streaming (with tools). The client picks based on whether `handleToolCall` was provided. This avoids forcing all Ask calls through streaming when most don't need it.

#### Message size and compression

The `context` field in `AskRequest` carries assembled code context — typically 30k-50k tokens (~120k-200k bytes). `EmbedRequest` carries grouped chunk text for an entire batch.

| Setting | Value | Rationale |
|---------|-------|-----------|
| Max receive message size | 8 MB | Accommodates large context + embedding batches with headroom |
| Max send message size | 8 MB | Embedding responses (1024 floats x thousands of chunks) |
| Compression | gzip (client and server) | ~4x reduction on text-heavy payloads; negligible CPU cost |
| Over-limit error | `RESOURCE_EXHAUSTED` + "Context too large (X bytes, max 8MB) — reduce scope or token budget" | Actionable |

Both client and server configure these limits. Gzip compression means most requests use ~25% of their raw size on the wire.

For large embedding batches that would exceed the message limit, the client paginates: `IChunkEmbeddingProvider.EmbedContextualizedAsync` splits grouped chunks into pages that fit within the message size, makes multiple `EmbedContextualized` calls, and merges results. The service handles Voyage API batching internally (120k token / 16k chunk limits per Voyage call). Two layers of batching, both transparent to the caller.

### Provider Clients

Two HTTP clients, both stateless. No connection pooling beyond what `HttpClient` provides.

#### Grok (xAI)

- **API:** OpenAI-compatible (`POST /v1/chat/completions`)
- **Streaming:** SSE with `stream: true` — service consumes chunks and assembles the response
- **Tool calling:** OpenAI function calling format — service defines tool schemas, Grok invokes them
- **Models:** `grok-4.1-fast` for all calls. Non-reasoning for keywords, reasoning for synthesis (controlled by system prompt)
- **Auth:** `Authorization: Bearer $XAI_API_KEY`

#### Voyage

- **Embedding API:** `POST /v1/contextualizedembeddings` — grouped chunks in, vectors out
- **Reranking API:** `POST /v2/rerank` — query + documents in, scored list out
- **Auth:** `Authorization: Bearer $VOYAGE_API_KEY`
- **Batching:** Service handles splitting large requests to stay within Voyage's per-request limits (120k tokens, 16k chunks)
- **Partial failure:** If some chunks in a batch fail (e.g., token limit exceeded for a single document), the response includes successful embeddings and per-chunk error indicators. The host retries or skips failed chunks — the batch is never discarded entirely

Both clients are internal — no interface abstraction needed. The service is committed to these providers. Testability comes from integration tests against real APIs (with budget caps), not mocks behind interfaces.

### The Ask Relay

The service's core job: sit between the host and Grok, managing state and tool calls.

#### Unary Ask (no tools)

```
Host → Service:  AskRequest { context, question, maxTokens, includeReasoning }
Service → Grok:  POST /v1/chat/completions (stream: true)
Grok → Service:  SSE chunks (content + optional reasoning)
Service → Host:  AskResponse { content, reasoning? }
```

The service streams from Grok internally (SSE) and assembles the response. The host gets a single unary response. All use cases are request-response — no host-facing streaming needed.

#### Bidirectional Ask (with tools)

```
Host → Service:    AskRequest { context, question, maxTokens }
Service → Grok:    POST /v1/chat/completions (stream: true, tools: [...])

  Loop (max 3 rounds):
    Grok → Service:    tool_call { name, arguments }
    Service → Host:    ToolCall { id, name, arguments }
    Host → Service:    ToolCallResult { id, content }
    Service → Grok:    POST /v1/chat/completions (messages += tool result)

Grok → Service:    content (final answer)
Service → Host:    AskResponse { content, reasoning? }
```

The service maintains conversation state for the duration of the stream: messages accumulate as tool calls resolve. Each Grok call is a fresh HTTP request with the full message history — this is how OpenAI-compatible APIs work.

**Tool schemas** are defined by the service, not the host. The service tells Grok what tools exist (read_uri, search, snippet, etc.) and their parameter schemas. When Grok calls one, the service relays to the host. The host resolves locally and returns the result. The service never interprets tool call arguments — it's a pass-through.

**Max 3 tool call rounds.** After 3 rounds, the service instructs Grok to synthesize with what it has. This bounds latency and cost.

### Authentication

Two token types, two validation paths. See [llm-service-integration.md](llm-service-integration.md#authentication) for the client-side perspective.

#### Access tokens (JWT)

- Issued by the service at login and token refresh
- Short-lived (~1 hour)
- Signed with a service-owned key (RS256)
- Claims: `account_id` only — minimal, stable across plan changes
- **Validated without DB lookup** — signature check resolves the account
- Entitlement (plan, capabilities, limits) checked from in-memory cache, refreshed from DB every 60 seconds and on billing webhook events

#### Refresh tokens

- Issued at login alongside the access token
- Long-lived, stored hashed in PostgreSQL
- Used only at `POST /token/refresh` — never sent over gRPC
- Returns a new access token (and rotates the refresh token)
- Revocable server-side (revoke = delete from DB)

#### API keys (operator-issued)

- Issued manually by the service operator
- Long-lived, stored hashed in PostgreSQL
- Sent directly as bearer token on gRPC calls
- **Validated with DB lookup** (cached — TTL 5 minutes)

#### gRPC auth interceptor

Every gRPC call:

1. Extract `authorization: Bearer <token>` from metadata
2. Try JWT validation (signature check, expiry) → extract `account_id`
3. If not a valid JWT → try API key lookup (cached, TTL 5 min) → resolve `account_id`
4. If neither → reject with `UNAUTHENTICATED` + `"Run ::login to enable cloud features"`
5. Look up entitlement from in-memory cache (account_id → plan, capabilities, usage counters)
6. Check: plan includes this capability? Under usage limit?
7. If no → reject with `PERMISSION_DENIED` + actionable message
8. If yes → forward to provider, record usage

JWT-first means most calls (interactive users with refresh tokens) resolve the account without any DB lookup. Entitlement cache is shared across all calls and refreshed on billing events, so plan changes take effect within seconds.

### Login Flow

CLI and web users need different OAuth flows.

#### CLI: Device authorization

`::login` or `repoql login`:

1. Client requests a device code from the service (`POST /auth/device`)
2. Service returns a `user_code` and `verification_uri`
3. Client displays: "Visit repoql.ai/device and enter code: ABCD-1234"
4. User opens browser, enters code, authenticates with GitHub/Google/Apple
5. Client polls `POST /auth/device/token` until user completes auth
6. Service issues access token + refresh token
7. Client stores in `~/.repoql/credentials`

Device flow works in SSH sessions, containers, and environments without localhost access. GitHub and Google support it natively. Apple requires a web redirect (handle as a fallback for Apple-only users).

**Security controls:**
- Device codes: high entropy (cryptographic random), 15-minute TTL, single-use, rate-limited polling (5s interval enforced server-side)
- Web OAuth: PKCE (S256) on all flows, `state` parameter with HMAC verification, short-lived authorization codes
- Webhook endpoints: signature verification (Stripe `Stripe-Signature`, GitHub `X-Hub-Signature-256`), idempotency keys to handle redelivery, replay protection via timestamp validation

#### Web: Standard OAuth

Users visiting `repoql.ai`:

1. Click "Sign in with GitHub/Google/Apple"
2. Standard OAuth redirect flow
3. On callback, service creates or finds account
4. If subscribing: redirect to Stripe Checkout or GitHub Marketplace
5. After subscription: issue access token + refresh token, display for CLI configuration

### Metering

Usage tracking must be accurate enough for billing but must never block the request path.

#### Write path

1. gRPC call completes successfully
2. Handler enqueues a usage event: `{ account_id, type, tokens_in, tokens_out, timestamp }`
3. Background writer flushes events to PostgreSQL in batches (every 5 seconds or 100 events)

The event queue is in-memory. If the service crashes, unbatched events are lost. This is acceptable — usage is for billing (monthly aggregates), not audit. The loss window is at most 5 seconds.

#### Read path (limit checks)

1. Auth interceptor needs to check: is this account under its plan limit?
2. Usage counters are cached in-memory per account, seeded from `usage_counters` table on first access
3. Incremented locally on each request (slightly optimistic between DB flushes)

Over-serving by a few requests at the period boundary is acceptable. Under-serving (rejecting a valid request because the cache is stale) is not — cache refresh is eager, not lazy.

### Database

Managed PostgreSQL. Seven tables.

```sql
accounts (
    id              uuid PRIMARY KEY,
    created_at      timestamptz NOT NULL
)

identities (
    id              uuid PRIMARY KEY,
    account_id      uuid REFERENCES accounts,
    provider        text NOT NULL,       -- 'github', 'google', 'apple'
    provider_id     text NOT NULL,       -- provider's user ID
    email           text,
    UNIQUE(provider, provider_id)
)

refresh_tokens (
    token_hash      bytea PRIMARY KEY,   -- SHA-256 of token
    account_id      uuid REFERENCES accounts,
    expires_at      timestamptz NOT NULL,
    revoked_at      timestamptz           -- NULL = active
)

api_keys (
    key_hash        bytea PRIMARY KEY,   -- SHA-256 of key
    account_id      uuid REFERENCES accounts,
    label           text,                -- operator's note
    created_at      timestamptz NOT NULL,
    revoked_at      timestamptz           -- NULL = active
)

entitlements (
    id              uuid PRIMARY KEY,
    account_id      uuid REFERENCES accounts,
    source          text NOT NULL,       -- 'stripe', 'marketplace', 'allowlist'
    plan            text NOT NULL,       -- 'pro', 'team'
    billing_id      text,                -- Stripe customer ID or Marketplace subscription ID
    valid_until     timestamptz           -- NULL = active indefinitely (until cancelled)
)

usage_events (
    id              bigserial PRIMARY KEY,
    account_id      uuid NOT NULL,
    event_type      text NOT NULL,       -- 'ask', 'rerank', 'embed'
    tokens_in       int NOT NULL DEFAULT 0,
    tokens_out      int NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now()
)

-- Incremental usage counters (updated in the write path alongside usage_events)
usage_counters (
    account_id      uuid NOT NULL,
    period          date NOT NULL,       -- first day of month
    event_type      text NOT NULL,
    call_count      bigint NOT NULL DEFAULT 0,
    total_tokens_in bigint NOT NULL DEFAULT 0,
    total_tokens_out bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (account_id, period, event_type)
);
```

**Why no `plan` column on `accounts`:** Entitlement is a separate concern from identity. An account exists because someone signed in. An entitlement exists because they paid. Keeping them separate means accounts survive plan changes, cancellations, and provider switches cleanly.

### Bootstrap: Allowlist

Before billing ships, access is controlled by a GitHub username allowlist (see [auth-and-billing.md](../../flows/future/llm-service/auth-and-billing.md#bootstrap-allowlist-access)). This is an entitlement with `source = 'allowlist'` — no special code path. When billing ships, remove allowlist entries and the mechanism that creates them.

---

## Cross-Cutting Concerns

### Error propagation

Errors from providers pass through to the host as gRPC status codes with descriptive messages. The service never swallows provider errors.

| Origin | gRPC status | Message pattern |
|--------|-------------|----------------|
| Provider timeout | `DEADLINE_EXCEEDED` | "Grok timed out — retry or reduce context size" |
| Provider rate limit | `RESOURCE_EXHAUSTED` | "Provider rate limited — retrying automatically" (service retries once) |
| Provider error | `INTERNAL` | "Grok returned an error — try again" |
| Auth failure | `UNAUTHENTICATED` | "Run ::login to enable cloud features" |
| No entitlement | `PERMISSION_DENIED` | "Cloud features require a RepoQL subscription — repoql.ai" |
| Limit exceeded | `PERMISSION_DENIED` | "2000/2000 explain calls used — upgrade at repoql.ai/upgrade" |

Every error is actionable. The host passes these messages through to the agent as content (see [integration design, error handling](llm-service-integration.md#error-handling)).

### Observability

OpenTelemetry throughout. Every gRPC call emits a span with:
- `account_id`, `plan`, `event_type`
- `provider` (grok/voyage), `model`, `tokens_in`, `tokens_out`
- `duration_ms`, `status_code`

Logs structured as JSON. Metrics exported to whatever the hosting provider offers. In development, traces connect to the Aspire dashboard.

### Provider health

The service checks provider health at startup (lightweight probe per provider — see [service-lifecycle.md](../../flows/future/llm-service/service-lifecycle.md)). After startup, provider health is inferred from real calls: consecutive failures trigger a circuit breaker that fast-fails requests for that capability until a probe succeeds. Same pattern as the client-side circuit breaker, but at the service level.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| ASP.NET Core | Go, Node | Same ecosystem as host codebase; first-class gRPC; shared types possible |
| JWT access tokens (stateless) | DB-validated sessions | No DB roundtrip on every gRPC call; most calls are interactive users with refresh tokens |
| API keys validated via cached DB lookup | API keys as JWTs | Operator needs to revoke immediately; cache TTL (5 min) is acceptable lag |
| In-memory usage event queue | Synchronous DB writes | Never block the request path on metering; ~5s loss window is acceptable for monthly billing |
| Incremental `usage_counters` table | Aggregation queries on `usage_events` | Counters updated in write path (upsert); limit checks are a single row read, not a scan |
| Two Ask RPCs (unary + bidi) | Single bidi RPC for all | Most Ask calls don't use tools; unary is simpler for the common case |
| Device flow for CLI login | Localhost redirect | Works in SSH, containers, codespaces — everywhere CLI users are |
| No provider abstraction interfaces | Internal IGrokClient/IVoyageClient | Committed to these providers; interfaces add indirection for swappability we don't need |

## Alternatives Considered

**Cloudflare Workers for the whole service.** Workers handle HTTP well and run at the edge, but don't support gRPC. We'd need to wrap gRPC in HTTP/JSON (gRPC-Web or a REST adapter), adding a translation layer. The service is better as a standard gRPC server — Cloudflare handles DNS and the landing page.

**Separate services per capability.** One service for synthesis, one for embedding/reranking. This adds deployment complexity without benefit — the capabilities share auth, metering, and database. One service, one deploy.

**Event sourcing for usage.** Instead of simple event rows, use an append-only event store with projections. Overkill — we're counting calls per month, not rebuilding state. Simple inserts with incremental counter upserts.

**Redis for session/token cache.** Would allow multi-instance deployments to share cache. Not needed yet — single instance handles the expected load. Add Redis when scaling demands it (same time as the content-addressable cache for teams).

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Provider API changes | Low | Calls fail until client updated | Pin to stable API versions; monitor changelogs |
| Single instance limits throughput | Medium | Requests queue during spikes | Monitor latency; scale to multiple instances when needed (JWT auth is stateless, usage events can fan out) |
| Grok rate limits during spike | Medium | Ask calls fail | Retry once; circuit breaker; queue if needed |
| JWT signing key compromise | Low | All access tokens valid until rotated | Key rotation support from day one; short token lifetime limits blast radius |
| Unbatched usage events lost on crash | Low | Under-count by ~5s of traffic | Acceptable for monthly billing; reconcile against provider usage dashboards |
| Device flow not supported by Apple | Medium | Apple sign-in only via web | Fall back to web-based OAuth for Apple identity; device flow for GitHub and Google |

## Extension Points

| Point | What it enables |
|-------|----------------|
| Content-addressable cache (Redis/Valkey) | Near-zero marginal cost for teams on same codebase — see [service-lifecycle.md open question](../../flows/future/llm-service/service-lifecycle.md#open-question-content-addressable-caching) |
| Additional providers | New provider = new HTTP client + updated tool schemas. No architectural change |
| Webhook reconciliation jobs | Periodic polling of Stripe/Marketplace APIs to catch missed webhooks |
| Multi-instance deployment | JWT auth is stateless; usage events need shared store (Redis or DB-direct); refresh tokens already in DB |
