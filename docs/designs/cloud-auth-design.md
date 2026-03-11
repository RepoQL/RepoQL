# Cloud Service Authentication — Design

## North Star

One API key, one config line, works immediately. Identity and access grow without changing the client. The simplest auth that earns trust — no setup friction, no silent failures, no lock-in.

## Context

RepoQL cloud services (`api.repoql.ai`) expose embedding and inference over gRPC. Today, auth is a static list of SHA-256 key hashes in Cloud Run config — adding or revoking a key requires redeployment. There's no per-user identity, no usage tracking, no self-service key management.

Two auth methods are needed:

1. **API keys** — long-lived opaque tokens for programmatic access (the Stripe/OpenAI pattern). This is the primary method — every RepoQL host uses it.
2. **Sessions** — OAuth2 access tokens for interactive use (dashboard, future CLI login). Secondary — humans only.

**Informed by:** `docs/designs/workos-auth-research.md`

## Constraints

| Constraint | Why |
|------------|-----|
| Pre-revenue | Free tiers matter. Can't spend $125/connection/month on SSO yet. |
| gRPC latency | Per-request external API calls for validation are unacceptable. Validation must be local. |
| Single writer | Firestore writes from Cloud Run only. RepoQL hosts are read-only consumers via gRPC. |
| Existing interceptor works | `ApiKeyAuthInterceptor` already does SHA-256 validation. Extend, don't replace. |
| No client changes for API keys | `Authorization: Bearer <key>` is already the wire format. Clients don't change. |
| Laptop-first | RepoQL hosts never talk to Firestore or WorkOS directly. All auth resolves server-side. |

---

## Design

### Token Discrimination

The interceptor receives `Authorization: Bearer <token>` and must determine which validation path to use. Two token types, distinguished by format:

| Token type | Format | Validation |
|------------|--------|------------|
| API key | Opaque, prefixed `rql_` | SHA-256 hash → Firestore lookup |
| Access token | JWT (three dot-separated base64 segments) | Signature + claims validation |

The interceptor examines the token. If it starts with `rql_`, it's an API key. If it decodes as a JWT header, it's a session token. Anything else is rejected.

This is unambiguous — the prefix `rql_` can never be valid base64-encoded JSON, so the two formats never collide.

### API Keys

**Format:** `rql_` + 32 random bytes, base62-encoded. Example: `rql_7kX2mP9qR4nL5wY8...` (~48 characters total).

**Lifecycle:**

```
Create key → return plaintext once → store SHA-256 hash + metadata in Firestore
                                                    ↓
Client sends key → interceptor hashes → lookup in cache → allow/deny
                                                    ↑
                            Firestore watch stream keeps cache warm
```

**Storage (Firestore):**

Collection: `api-keys`

```
{
  "hash": "a3f2...",           // SHA-256 hex, also the document ID
  "prefix": "rql_7kX2",       // first 8 chars, for identification in UI
  "name": "stuart-laptop",    // user-chosen label
  "org_id": null,              // future: organization scope
  "created_at": "2026-03-12T...",
  "last_used_at": null,
  "revoked": false,
  "scopes": ["embedding", "inference"]  // future: granular permissions
}
```

Document ID is the SHA-256 hash — lookups are O(1).

**Cache:** Cloud Run service maintains an in-memory `HashSet<string>` of valid (non-revoked) key hashes, populated on startup from Firestore and kept current via Firestore real-time listeners. Cache miss falls through to a direct Firestore read (cold path for newly-created keys before the listener fires).

**Key management:** Initially via a `::cloud-keys` command or admin gRPC endpoint. Future: WorkOS API Keys widget for self-service.

### Session Tokens (WorkOS)

**Provider:** WorkOS — free up to 1M MAU, OAuth2/PKCE, organizations, SSO-ready.

**Flow:**

```
User → WorkOS AuthKit → authorization code → Cloud Service exchanges for tokens
                                                      ↓
                                            Access token (JWT, 5min) + refresh token (30 days)
                                                      ↓
Client sends JWT → interceptor validates signature + expiry + claims → allow/deny
```

**JWT validation** uses WorkOS JWKS endpoint, cached locally with standard rotation. No per-request WorkOS API call — just local signature verification using `Microsoft.IdentityModel.Tokens`.

**Claims extracted:**

| Claim | Use |
|-------|-----|
| `sub` | User ID |
| `org_id` | Organization (future: scoped access) |
| `exp` | Expiry |
| `permissions` | Future: granular access control |

**Refresh tokens** are handled client-side. The dashboard or CLI detects a 401, uses the refresh token to get a new access token from WorkOS, retries. The gRPC service never sees refresh tokens.

### Interceptor Architecture

Extend the existing `ApiKeyAuthInterceptor`:

```
                    ┌─────────────────────────┐
                    │   AuthInterceptor       │
                    │                         │
Bearer token ──────►  Discriminate by format  │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  API key?    JWT?       │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  Hash+lookup  Validate  │
                    │  (Firestore   (JWKS     │
                    │   cache)      cache)    │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  Set identity on context │
                    └─────────────────────────┘
```

Both paths produce an `AuthIdentity` on the call context:

```csharp
public record AuthIdentity(
    string Id,              // key hash or user ID
    AuthMethod Method,      // ApiKey or Session
    string? OrgId,          // null until org support
    string[] Scopes         // ["embedding", "inference"]
);

public enum AuthMethod { ApiKey, Session }
```

Downstream services read identity from context — they don't care how auth happened.

### Bypass Hatch

The existing behavior (empty `ApiKeyHashes` = open) is preserved as a development mode. In production, at least one key hash must exist or the Firestore listener must be configured. A startup health check logs a warning if auth is effectively disabled.

### Infrastructure (Pulumi)

New resources:

| Resource | Purpose |
|----------|---------|
| Firestore collection `api-keys` | Key hash storage |
| Secret Manager `workos-api-key` | WorkOS API key for server-side SDK |
| Secret Manager `workos-client-id` | WorkOS client ID |

Firestore already exists in both environments. The `api-keys` collection is created implicitly on first write (Firestore is schemaless).

### Configuration

Cloud Run environment variables (via Secret Manager):

```
Auth__WorkOs__ApiKey=wos_...
Auth__WorkOs__ClientId=client_...
Auth__Firestore__ProjectId=repoql-production
Auth__Firestore__ApiKeysCollection=api-keys
```

RepoQL host config (unchanged):

```
Cloud.ApiKey=rql_7kX2mP9qR4nL5wY8...
```

No client-side changes. The host doesn't know or care whether it's using a Firestore-backed key or a static hash.

---

## Cross-Cutting Concerns

### Usage Tracking

Both auth paths write to the existing `product-analytics` Firestore collection. Per-request: key prefix (not the key), method called, token count, latency. Aggregated server-side, not per-call.

### Rate Limiting

Per-key rate limiting via in-memory sliding window on Cloud Run. Not per-user initially — API keys are the unit of rate limiting. Limits configurable per key in Firestore metadata.

### Key Rotation

Users create a new key, update their config, then revoke the old key. No grace period complexity — the old key works until explicitly revoked. Revocation propagates via Firestore listener within seconds.

### Error Messages

| Scenario | gRPC Status | Detail |
|----------|-------------|--------|
| No Authorization header | `UNAUTHENTICATED` | "Missing authorization header. Set Cloud.ApiKey in RepoQL config." |
| Malformed token | `UNAUTHENTICATED` | "Invalid token format. Expected API key (rql_...) or JWT." |
| Unknown API key | `UNAUTHENTICATED` | "API key not recognized. Check Cloud.ApiKey value." |
| Revoked API key | `PERMISSION_DENIED` | "API key has been revoked." |
| Expired JWT | `UNAUTHENTICATED` | "Session expired. Re-authenticate." |
| Invalid JWT signature | `UNAUTHENTICATED` | "Invalid session token." |

Every error is actionable — the client (or the agent configuring it) can self-recover.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| DIY API keys + Firestore | WorkOS API Keys | Local validation (no per-request API call), no SDK dependency risk, full control |
| WorkOS for sessions | DIY OAuth2 | Don't build identity infrastructure; free 1M MAU; SSO-ready when needed |
| Firestore real-time listener | Polling / webhook | Firestore already provisioned; listener is push-based, low-latency, built-in |
| `rql_` prefix | No prefix | Unambiguous discrimination from JWTs; familiar pattern (Stripe `sk_`, OpenAI `sk-`) |
| In-memory cache | Redis / Memcached | Single Cloud Run instance; no additional infrastructure |
| Per-key rate limiting | Per-user | API keys are the billing/access unit; simpler; per-user comes with org support |

## Alternatives Considered

**WorkOS API Keys for everything:** The Oct 2025 feature provides org-scoped keys with an embeddable widget. However: (1) .NET SDK support for the API Keys feature is unverified, (2) validation would require a WorkOS API call per request (unacceptable for gRPC latency), (3) we'd depend on WorkOS for the critical path of every embedding/inference call. WorkOS API Keys could be a future self-service layer that writes to our Firestore store.

**Unkey:** Purpose-built API key management with rate limiting built in. Attractive, but adds another dependency and another network hop for validation. Better fit at higher scale or if we need sophisticated per-key rate limiting beyond simple sliding windows.

**Static hashes only (status quo):** Works but doesn't scale. Adding a key requires redeployment. No revocation, no usage tracking, no self-service.

**JWT-only (no API keys):** The Node.js/web pattern. But API keys are the dominant pattern for API services (Stripe, OpenAI, Anthropic) because they're simpler for programmatic use — no token refresh, no OAuth dance. And RepoQL hosts are machines, not browsers.

## Risks

| Risk | Mitigation |
|------|------------|
| Firestore listener drops | Fallback to direct read on cache miss; periodic full refresh as backstop |
| WorkOS .NET SDK lags features | JWT validation uses standard libraries, not WorkOS SDK; session management is secondary |
| Single Cloud Run instance = single cache | Cloud Run scales to multiple instances; each instance maintains its own cache via Firestore listener. Consistent because Firestore is the source of truth |
| Key leaked | Revocation propagates in seconds via listener. Usage tracking surfaces anomalies. Future: key expiration |
| WorkOS outage | API keys (primary method) don't depend on WorkOS at all. Only session tokens are affected |

## Extension Points

| Point | What it enables |
|-------|----------------|
| `AuthIdentity.OrgId` | Organization-based access control, usage quotas per org |
| `scopes` array | Granular permissions (embedding-only keys, read-only keys) |
| WorkOS API Keys widget | Self-service key management UI without building a dashboard |
| Firestore key metadata | Per-key rate limits, expiration dates, usage caps |
| Additional token types | Future discriminator patterns (e.g., `rql_temp_` for temporary keys) |

## Phasing

**Phase 1 — Dynamic API keys (immediate):**
- Firestore-backed key store with real-time cache
- Extend interceptor for Firestore lookup
- Admin endpoint or command for key CRUD
- Usage tracking per key
- Remove static `ApiKeyHashes` config dependency

**Phase 2 — WorkOS identity (when dashboard exists):**
- WorkOS integration for user login
- JWT validation in interceptor
- Session management for dashboard
- CLI `repoql login` flow

**Phase 3 — Self-service (when orgs exist):**
- WorkOS organizations for multi-tenancy
- WorkOS API Keys widget or custom key management UI
- Per-org usage quotas and billing
