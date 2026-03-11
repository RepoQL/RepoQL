# Cloud Service Authentication — Design

## North Star

`repoql login` → never think about auth again. The tool handles tokens, refresh, and recovery invisibly. API keys exist for CI and scripts, but the default path is: log in once, use forever.

## Context

RepoQL cloud services (`api.repoql.ai`) expose embedding and inference over gRPC. Today, auth is a static list of SHA-256 key hashes in Cloud Run config — adding or revoking a key requires redeployment. There's no per-user identity, no usage tracking, no self-service.

Two auth methods, one primary:

1. **Sessions (primary)** — User logs in once via CLI, gets a refresh token, access tokens rotate silently. The `gh auth login` model. This is how every human uses RepoQL.
2. **API keys (secondary)** — Long-lived opaque tokens for CI/CD, scripts, and non-interactive use. The Stripe/OpenAI pattern. Fallback for machines.

**Informed by:** `docs/research/workos-auth-research.md`

## Constraints

| Constraint | Why |
|------------|-----|
| Pre-revenue | Free tiers matter. Can't spend $125/connection/month on SSO yet. |
| gRPC latency | Per-request external API calls for validation are unacceptable. Validation must be local. |
| Single writer | Firestore writes from Cloud Run only. RepoQL hosts are read-only consumers via gRPC. |
| Existing interceptor works | `ApiKeyAuthInterceptor` already does SHA-256 validation. Extend, don't replace. |
| Laptop-first | RepoQL hosts never talk to Firestore or WorkOS directly. All auth resolves server-side. |
| Login once | The user authenticates once. Everything after that is invisible. |

---

## Design

### The Login Flow

```
repoql login
    │
    ▼
Open browser → WorkOS AuthKit (hosted login page)
    │
    ▼
User authenticates (email, Google, GitHub, etc.)
    │
    ▼
Callback to localhost with authorization code
    │
    ▼
CLI exchanges code for access token (JWT, 5min) + refresh token (30 days)
    │
    ▼
Store refresh token in OS credential store (Windows Credential Manager / macOS Keychain / Linux Secret Service)
Store access token in memory + ~/.repoql/auth.json (short-lived, ok on disk)
    │
    ▼
Done. User never thinks about auth again.
```

### Silent Token Refresh

The RepoQL host manages tokens transparently:

```
gRPC call → check access token expiry
    │
    ├── Valid (>30s remaining) → attach to request, proceed
    │
    ├── Expiring soon (<30s) → refresh in background, use current token
    │
    └── Expired → refresh synchronously, retry request
            │
            ├── Refresh succeeds → new access token, proceed
            │
            └── Refresh fails (revoked/expired) → prompt: "Session expired. Run: repoql login"
```

The refresh token (30 days) means a user who uses RepoQL daily never re-authenticates. If they go on a month-long holiday, one `repoql login` on return.

### Token Discrimination

The interceptor receives `Authorization: Bearer <token>` and routes by format:

| Token type | Format | Validation | Primary use |
|------------|--------|------------|-------------|
| Access token | JWT (three dot-separated base64 segments) | JWKS signature + claims | Interactive (CLI, dashboard) |
| API key | Opaque, prefixed `rql_` | SHA-256 hash → Firestore lookup | CI/CD, scripts |

The prefix `rql_` can never be valid base64-encoded JSON — formats never collide.

### Session Auth (WorkOS) — Primary

**Provider:** WorkOS — free up to 1M MAU, OAuth2/PKCE, organizations, SSO-ready.

**JWT validation** uses WorkOS JWKS endpoint, cached locally with standard rotation. No per-request WorkOS API call — just local signature verification using `Microsoft.IdentityModel.Tokens`.

**Claims extracted:**

| Claim | Use |
|-------|-----|
| `sub` | User ID (identity for usage tracking, rate limiting) |
| `org_id` | Organization (future: scoped access) |
| `exp` | Expiry |
| `permissions` | Future: granular access control |

**Client-side token storage:**

| Token | Storage | Lifetime |
|-------|---------|----------|
| Refresh token | OS credential store (encrypted) | 30 days (configurable in WorkOS) |
| Access token | `~/.repoql/auth.json` + memory | 5 minutes |

The refresh token never leaves the local machine, never hits the gRPC service, never gets logged.

### API Keys (WorkOS API Keys) — Secondary

WorkOS launched API Keys in October 2025 — org-scoped, with webhook events for lifecycle management.

**Flow:**

```
User creates key via WorkOS dashboard/widget or CLI command
    │
    ▼
WorkOS stores key, emits api_key.created webhook
    │
    ▼
Cloud Service receives webhook → caches key hash in Firestore
    │
    ▼
Client sends key → interceptor hashes → lookup in Firestore cache → allow/deny
```

**Why WorkOS API Keys over DIY:**
- Self-service key management UI (embeddable widget) without building a dashboard
- Org-scoped keys come free with WorkOS organizations
- Webhook-based cache means validation is still local (no per-request WorkOS call)
- One auth provider for both sessions and API keys — simpler to reason about

**Fallback if WorkOS API Keys .NET SDK is incomplete:** DIY keys with `rql_` prefix, Firestore storage, same interceptor path. The wire format is identical either way — only the management layer changes.

**Firestore cache (populated via webhooks):**

Collection: `api-keys`

```
{
  "hash": "a3f2...",           // SHA-256 hex, also the document ID
  "prefix": "rql_7kX2",       // first 8 chars, for identification in UI
  "name": "ci-pipeline",      // user-chosen label
  "user_id": "user_...",      // WorkOS user who created it
  "org_id": null,              // future: organization scope
  "created_at": "2026-03-12T...",
  "last_used_at": null,
  "revoked": false
}
```

Cache kept warm via Firestore real-time listeners. Cache miss falls through to direct Firestore read.

### Interceptor Architecture

Extend the existing `ApiKeyAuthInterceptor`:

```
                    ┌─────────────────────────┐
                    │   AuthInterceptor       │
                    │                         │
Bearer token ──────►  Discriminate by format  │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  rql_*?      JWT?       │
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
    string UserId,           // WorkOS user ID (from JWT sub or key's user_id)
    AuthMethod Method,       // ApiKey or Session
    string? OrgId,           // null until org support
    string DisplayName       // email or key name, for logging
);

public enum AuthMethod { ApiKey, Session }
```

Downstream services read identity from context — they don't care how auth happened.

### CLI Integration

New commands:

```
repoql login          # Opens browser, completes OAuth, stores tokens
repoql logout         # Clears stored tokens
repoql whoami         # Shows current user, org, auth method
repoql keys create    # Create an API key (for CI/scripts)
repoql keys list      # List active keys
repoql keys revoke    # Revoke a key
```

`repoql login` is the only auth command most users ever run.

### Bypass Hatch

The existing behavior (empty `ApiKeyHashes` = open) is preserved as a development mode. In production, at least one auth method must be configured. A startup health check logs a warning if auth is effectively disabled.

### Infrastructure (Pulumi)

New resources:

| Resource | Purpose |
|----------|---------|
| Secret Manager `workos-api-key` | WorkOS API key for server-side SDK |
| Secret Manager `workos-client-id` | WorkOS client ID |

Firestore `api-keys` collection created implicitly on first webhook write.

### Configuration

Cloud Run environment variables (via Secret Manager):

```
Auth__WorkOs__ApiKey=wos_...
Auth__WorkOs__ClientId=client_...
Auth__WorkOs__WebhookSecret=whsec_...
Auth__Firestore__ProjectId=repoql-production
```

RepoQL host config — after `repoql login`, managed automatically:

```
Cloud.AuthToken=eyJhbG...          # access token (auto-refreshed)
Cloud.RefreshToken=<stored in OS credential manager>
```

Legacy `Cloud.ApiKey` setting continues to work for API keys and backward compatibility.

---

## Cross-Cutting Concerns

### Usage Tracking

Both auth paths write to the existing `product-analytics` Firestore collection. Per-request: user ID, method called, token count, latency. Aggregated server-side, not per-call. Per-user tracking enables future billing.

### Rate Limiting

Per-user rate limiting via in-memory sliding window on Cloud Run. Both auth methods resolve to a user ID — same limits regardless of method. Limits configurable per user/org in Firestore metadata.

### Error Messages

| Scenario | gRPC Status | Detail |
|----------|-------------|--------|
| No Authorization header | `UNAUTHENTICATED` | "Not authenticated. Run: repoql login" |
| Malformed token | `UNAUTHENTICATED` | "Invalid token format. Run: repoql login" |
| Expired JWT, refresh works | *(handled client-side, user never sees this)* | |
| Expired JWT, refresh fails | `UNAUTHENTICATED` | "Session expired. Run: repoql login" |
| Unknown API key | `UNAUTHENTICATED` | "API key not recognized. Check key or run: repoql login" |
| Revoked API key | `PERMISSION_DENIED` | "API key has been revoked. Create a new one: repoql keys create" |
| Invalid JWT signature | `UNAUTHENTICATED` | "Invalid session. Run: repoql login" |

Every error points the user to the one command that fixes it.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Sessions as primary | API keys as primary | Users shouldn't manage keys. Log in once, forget about it. |
| WorkOS API Keys | DIY API keys | Self-service widget, org-scoping, webhook lifecycle — don't build what exists |
| Webhook → Firestore cache | Direct WorkOS API validation | Local validation, no per-request latency hit |
| OS credential store | Flat file for refresh token | Refresh tokens are long-lived secrets — encrypt at rest |
| WorkOS for everything | Mixed providers | One identity provider, one set of user IDs, one org model |
| Background token refresh | Refresh on 401 only | User never experiences auth latency |

## Alternatives Considered

**API keys as primary:** The Stripe/OpenAI model. But those are API-first products where the user IS a developer configuring integrations. RepoQL users are developers using a tool — they want to log in and forget, not manage keys. API keys exist for CI, not for daily use.

**DIY OAuth2:** Build the login flow ourselves against Google/GitHub. Works, but then we also build session management, token storage, user management, org support. WorkOS does all of this with a free tier of 1M MAU.

**DIY API keys + separate WorkOS sessions:** The previous design. But maintaining two independent auth systems (DIY Firestore keys + WorkOS JWTs) is more complex than using WorkOS for both. WorkOS API Keys unify the model — one provider, one user identity, one org boundary.

**Unkey for API keys:** Purpose-built, open source. But adds a second auth provider alongside WorkOS. If WorkOS API Keys prove insufficient, Unkey is the upgrade path.

## Risks

| Risk | Mitigation |
|------|------------|
| WorkOS API Keys .NET SDK incomplete | Fall back to DIY keys with same wire format. Interceptor doesn't change. |
| WorkOS outage blocks login | Existing sessions continue (refresh tokens are local). API keys work independently. Only new logins blocked. |
| OS credential store unavailable | Fall back to encrypted file in `~/.repoql/`. Warn user. |
| Refresh token stolen from disk | OS credential store encrypts at rest. Token is per-user, revocable from WorkOS dashboard. |
| Browser-based login fails (SSH/headless) | `repoql login --device-code` for device authorization flow. Or use API key. |

## Extension Points

| Point | What it enables |
|-------|----------------|
| `AuthIdentity.OrgId` | Organization-based access control, usage quotas per org |
| WorkOS Organizations | Multi-tenant billing, team management |
| WorkOS SSO | Enterprise customers federate identity ($125/connection when justified) |
| WorkOS API Keys widget | Embeddable self-service key management in dashboard |
| `repoql login --device-code` | Headless/SSH environments |

## Phasing

**Phase 1 — Login flow + JWT auth:**
- WorkOS integration for `repoql login`
- JWT validation in interceptor
- Silent token refresh in gRPC client
- OS credential store for refresh tokens
- `repoql logout` and `repoql whoami`
- Existing `Cloud.ApiKey` continues to work (backward compat)

**Phase 2 — WorkOS API Keys:**
- Webhook endpoint for key lifecycle events
- Firestore cache for key hashes
- `repoql keys create/list/revoke` commands
- Deprecate static `ApiKeyHashes` config

**Phase 3 — Organizations + billing:**
- WorkOS organizations for multi-tenancy
- Per-org usage quotas
- WorkOS API Keys widget in dashboard
- SSO for enterprise customers
