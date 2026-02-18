# LLM Service Failure Modes

What breaks, how to detect it, and how to recover. Every failure either self-heals or gives the agent a clear path forward.

## Provider Failures

### FM-01: Grok Rate Limited

**Trigger**: Too many synthesis requests in a time window
**Detection**: HTTP 429 from xAI API
**Impact**: Explain calls delayed
**Recovery**: Exponential backoff with jitter; service queues requests. Agent sees "synthesis delayed" in streaming response, not a failure.
**Prevention**: Request coalescing — if multiple hosts ask the same question within a window, serve from cache.

### FM-02: Grok Timeout

**Trigger**: Synthesis takes longer than deadline (e.g., 30s)
**Detection**: gRPC deadline exceeded
**Impact**: Agent's explain call fails
**Recovery**: Service returns partial synthesis if streaming started, or error with assembled context summary. Agent can retry or fall back to reading files directly.
**Prevention**: Set conservative deadlines; use non-reasoning variant for simpler questions.

### FM-03: Grok Unavailable

**Trigger**: xAI API is down
**Detection**: Connection refused or 5xx errors on health check
**Impact**: Explain and keyword extraction unavailable
**Recovery**: Service marks synthesis as degraded. RepoQL host skips explain, tells agent "explain temporarily unavailable — structural search and local semantic search still work." Background health checks restore when API recovers.

### FM-04: Voyage Rate Limited

**Trigger**: Too many embedding/rerank requests
**Detection**: HTTP 429 from Voyage API
**Impact**: Reranking delayed; batch embedding slowed
**Recovery**: Exponential backoff. Reranking falls back to local ranking (agent sees slightly lower quality, not an error). Batch embedding retries automatically.
**Prevention**: Batch efficiency — fewer larger requests, not many small ones.

### FM-05: Voyage Unavailable

**Trigger**: Voyage API is down
**Detection**: Connection refused or 5xx on health check
**Impact**: Reranking and cloud embedding unavailable
**Recovery**: Local ranking used for all search. Cloud embedding queued for when service recovers. Agent sees no difference in interactive search (local model is the foundation).

### FM-06: Voyage Model Deprecated

**Trigger**: Model version retired by Voyage
**Detection**: 404 or deprecation warning on API response
**Impact**: Embedding/reranking calls fail
**Recovery**: Service falls back to configured backup model. Operator alerted to update configuration. Existing embeddings remain valid until migration.

## Service Failures

### FM-07: Service Unreachable

**Trigger**: LLM service not running, network partition, or host misconfigured
**Detection**: gRPC connection refused on RepoQL host
**Impact**: All cloud features unavailable
**Recovery**: RepoQL host operates in local-only mode. Explain returns "Cloud features require a RepoQL subscription — repoql.ai." All structural queries, local semantic search, and JIT embedding work normally.
**Prevention**: Service health endpoint; host checks on startup and periodically.

### FM-08: Service Crash Mid-Request

**Trigger**: Service process dies during a streaming synthesis
**Detection**: gRPC stream broken
**Impact**: Agent sees partial answer
**Recovery**: Host detects broken stream, returns what was received with "[synthesis interrupted — partial result]" suffix. Agent can retry.

### FM-09: Authentication Failure

**Trigger**: Provider API key expired or revoked
**Detection**: HTTP 401/403 from provider
**Impact**: Affected provider's capabilities unavailable
**Recovery**: Service logs clear error with provider name. Health check marks provider as down with reason "authentication failed." Operator rotates key; service picks up new key on next health check cycle.

## Data Integrity Failures

### FM-10: Embedding Space Mismatch

**Trigger**: Cloud model upgrade produces incompatible embeddings
**Detection**: Search quality drops; cosine similarity scores anomalous
**Impact**: Search results degrade
**Recovery**: Service tags embeddings with model version. On model change, old embeddings are invalidated and re-embedded incrementally. During migration, searches query both spaces.
**Prevention**: Track model version per embedding; never mix spaces silently.

### FM-11: Partial Batch Embedding

**Trigger**: Some passages in a batch fail to embed (e.g., too long, encoding error)
**Detection**: Batch response includes per-passage success/failure
**Impact**: Some passages lack cloud embeddings
**Recovery**: Successful embeddings stored; failed passages logged with reason. Local embeddings remain as foundation. Failed passages retried on next idle cycle.

## Cost Failures

### FM-12: Unexpected Cost Spike

**Trigger**: Bug causes excessive API calls; large repo triggers more calls than expected
**Detection**: Cost tracking exceeds alert threshold
**Impact**: Budget overrun
**Recovery**: Hard cost limit stops further cloud calls. Service continues serving from local capabilities. Operator investigates and adjusts limits.
**Prevention**: Per-request cost tracking; configurable daily/monthly limits; alert thresholds at 50%, 80%, 100%.

## Summary

| ID | Failure | Self-heals? | Agent sees |
|----|---------|-------------|------------|
| FM-01 | Grok rate limited | Yes (backoff) | Delayed synthesis |
| FM-02 | Grok timeout | No | Partial result or error |
| FM-03 | Grok unavailable | Yes (health check) | "Explain temporarily unavailable" |
| FM-04 | Voyage rate limited | Yes (backoff) | Local ranking (transparent) |
| FM-05 | Voyage unavailable | Yes (health check) | Local ranking (transparent) |
| FM-06 | Voyage model deprecated | Partial (fallback model) | Operator alert |
| FM-07 | Service unreachable | No | "LLM service not connected" |
| FM-08 | Service crash | No | Partial result + retry hint |
| FM-09 | Auth failure | No | Provider marked down |
| FM-10 | Embedding mismatch | Yes (migration) | Temporary quality dip |
| FM-11 | Partial batch | Yes (retry) | No visible impact |
| FM-12 | Cost spike | Yes (limit) | Cloud features paused |
