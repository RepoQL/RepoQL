# Cloud Service Authentication — Design

## North Star

`repoql login` → never think about auth again. The tool handles tokens, refresh, and recovery invisibly. API keys exist for CI and scripts, but the default path is: log in once, use forever.

## Context

RepoQL cloud services (`api.repoql.ai`) expose embedding and inference over gRPC. Today, auth is a static list of SHA-256 key hashes in Cloud Run config — adding or revoking a key requires redeployment. There's no per-user identity, no usage tracking, no self-service.

The existing `rql_`-prefixed API keys are **transitional** — they'll be removed once all users have accounts (< 7 days). Don't future-proof them.

Two auth methods, one primary:

1. **Sessions (primary)** — User logs in once via CLI, gets a refresh token, access tokens rotate silently. The `gh auth login` model. This is how every human uses RepoQL.
2. **API keys (secondary)** — Long-lived opaque tokens for CI/CD, scripts, and non-interactive use. Managed via the WorkOS portal, not the CLI.

**Informed by:** `docs/research/workos-auth-research.md`

## Constraints

| Constraint | Why |
|------------|-----|
| Pre-revenue | Free tiers matter. Can't spend $125/connection/month on SSO yet. |
| gRPC latency | Per-request external API calls for validation are unacceptable. Validation must be local (JWKS public key). |
| Single writer | Firestore writes from Cloud Run only. RepoQL hosts are read-only consumers via gRPC. |
| Existing interceptor works | `ApiKeyAuthInterceptor` already does SHA-256 validation. Extend, don't replace. |
| Laptop-first | RepoQL hosts never talk to Firestore or WorkOS directly. All auth resolves server-side. |
| Login once | The user authenticates once. Everything after that is invisible. |
| Fail-closed | Auth must never be accidentally disabled. No-auth only in DEBUG builds — compile-time guarantee, not configuration. |

---

## Design

### The Login Flow

Two equally supported paths — browser for desktops, device code for everything else:

```
repoql login                          repoql login --device-code
    │                                     │
    ▼                                     ▼
Open browser → WorkOS AuthKit         Display: "Go to https://... and enter code: ABCD-1234"
    │                                     │
    ▼                                     ▼
User authenticates                    User authenticates in any browser
(email, Google, GitHub, etc.)         (any device — phone, laptop, remote machine)
    │                                     │
    ▼                                     ▼
Callback to localhost:PORT            CLI polls for completion
    │                                     │
    └──────────────┬──────────────────────┘
                   ▼
CLI receives access token (JWT, 5min) + refresh token (30 days)
                   │
                   ▼
Store refresh token in OS credential store
Store access token in memory + ~/.repoql/auth.json
                   │
                   ▼
Done. User never thinks about auth again.
```

**Localhost port selection:** The browser flow binds to an ephemeral port (OS-assigned). The port is encoded in the OAuth redirect URI. Avoids conflicts entirely.

**Device code flow** is first-class — not a fallback. It's the primary path for SSH, WSL, containers, and headless servers. Must be verified against WorkOS (does WorkOS support RFC 8628 Device Authorization Grant?). If not, implement a DIY device flow: generate a code, display a URL, poll for completion via WorkOS API.

### Silent Token Refresh

The **RepoQL host** (gRPC client) manages tokens transparently. The **Cloud Run service** (gRPC server) only validates — it never refreshes.

```
Host makes gRPC call → check access token expiry
    │
    ├── Valid (>30s remaining) → attach to request, proceed
    │
    ├── Expiring soon (<30s) → refresh in background, use current token
    │
    └── Expired → refresh synchronously, retry request
            │
            ├── Refresh succeeds → new access + new refresh token → persist both
            │
            └── Refresh fails (revoked/expired) → prompt: "Session expired. Run: repoql login"
```

**Refresh token rotation:** WorkOS (like most OAuth2 providers) rotates the refresh token on each use — the response includes a new refresh token alongside the new access token. The client must persist the new refresh token to the OS credential store on every refresh. The old refresh token becomes invalid.

**Concurrent hosts:** Multiple RepoQL hosts (different repos, same machine) share the OS credential store. To prevent refresh token rotation races:
- Hosts acquire a file lock (`~/.repoql/.auth-lock`) before refreshing.
- After acquiring the lock, re-read the access token from disk — another host may have already refreshed.
- If the token is now valid, release the lock and use it.
- If still expired, refresh, persist both tokens, release the lock.

The refresh token (30 days) means a user who uses RepoQL daily never re-authenticates. If they go on a month-long holiday, one `repoql login` on return.

### Token Discrimination (Server-Side)

The interceptor receives `Authorization: Bearer <token>` and routes by format:

| Token type | Format | Validation | Primary use |
|------------|--------|------------|-------------|
| Access token | JWT (three dot-separated base64 segments) | JWKS public key signature + claims | Interactive (CLI sessions) |
| Legacy API key | Opaque, prefixed `rql_` | SHA-256 hash lookup (transitional — removing in < 7 days) | Backward compat |

The prefix `rql_` can never be valid base64-encoded JSON — formats never collide.

**Unrecognized format** (neither JWT nor `rql_` prefix): Return `UNAUTHENTICATED` with "Unrecognized token format. Run: repoql login".

### Session Auth (WorkOS) — Primary

**Provider:** WorkOS — free up to 1M MAU, OAuth2/PKCE, organizations, SSO-ready.

**JWT validation** uses WorkOS JWKS endpoint. The Cloud Run service fetches the public keys at startup (`IHostedService`) and caches them with standard rotation via `Microsoft.IdentityModel.Tokens.ConfigurationManager<OpenIdConnectConfiguration>`. No per-request WorkOS API call — just local public key signature verification.

**JWKS warm-up:** Fetch on startup, not on first request. Cold-start penalty is a sub-second HTTP call to WorkOS, but it must not be on the hot path of a user's first gRPC call.

**Clock skew:** `TokenValidationParameters.ClockSkew` set to 5 minutes (the `Microsoft.IdentityModel` default). Cloud Run has NTP; developer laptops may drift.

**Claims extracted:**

| Claim | Use |
|-------|-----|
| `sub` | User ID (identity for usage tracking, rate limiting) |
| `org_id` | Organization (future: scoped access) |
| `exp` | Expiry |

**Client-side token storage:**

| Token | Storage | Lifetime |
|-------|---------|----------|
| Refresh token | OS credential store (encrypted) | 30 days (configurable in WorkOS) |
| Access token | `~/.repoql/auth.json` + memory | 5 minutes |

The refresh token never leaves the local machine, never hits the gRPC service, never gets logged.

**OS credential store fallback:** If the OS credential store is unavailable (headless Linux without libsecret/gnome-keyring), fall back to `~/.repoql/.credentials` encrypted with a machine-bound key derived from machine ID (`/etc/machine-id` on Linux, `DPAPI` on Windows). Warn the user.

### API Keys — Secondary

API keys are managed via the **WorkOS portal** (or a future RepoQL dashboard). No CLI key management — at current scale, the portal is sufficient.

**Phase 2 concern.** During Phase 1, existing `rql_`-prefixed keys continue working via SHA-256 hash lookup. Phase 2 replaces this with WorkOS-managed keys validated via Firestore cache.

**Phase 2 key creation flow:**
```
User creates key in WorkOS portal/dashboard
    │
    ├── Portal returns the full key to the user (only time it's visible)
    │
    ▼
Creation endpoint writes key hash to Firestore directly
(Webhooks supplement for lifecycle events: revocation, expiry — but
 the initial write happens synchronously at creation time, not via webhook,
 because the webhook payload doesn't contain the full key to hash)
    │
    ▼
Client sends key → interceptor hashes → Firestore lookup → allow/deny
```

### Interceptor Architecture

Extend the existing `ApiKeyAuthInterceptor` to handle all handler types (unary, server streaming, duplex streaming) and discriminate token formats:

```
                    ┌─────────────────────────┐
                    │   AuthInterceptor       │
                    │                         │
Bearer token ──────►  Discriminate by format  │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  rql_*?      JWT?       │
                    │  (legacy)               │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  Hash+lookup  Validate  │
                    │  (config     (JWKS     │
                    │   hashes)    public    │
                    │              key)      │
                    │     │           │       │
                    │     ▼           ▼       │
                    │  Set identity on context │
                    └─────────────────────────┘
```

**Handler coverage:** Override `UnaryServerHandler`, `ServerStreamingServerHandler`, and `DuplexStreamingServerHandler`. No current server streaming endpoints exist, but the interceptor must be complete — an unprotected handler type is a security gap waiting to happen.

**Bypass:** Only in DEBUG builds (`#if DEBUG`). The no-auth path doesn't exist in release binaries — it's a compile-time guarantee, not a runtime flag that can be misconfigured.

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

### Client-Side Credential Provider

Today, gRPC clients (`GrpcEmbeddingProvider`, inference client) inject a static `_apiKey` via constructor. This changes to a credential provider pattern:

```csharp
// Replaces static _apiKey string
public interface ICloudCredentialProvider
{
    /// Returns a valid access token, refreshing if needed.
    /// Acquires file lock, checks disk, refreshes via WorkOS if expired.
    Task<string> GetTokenAsync(CancellationToken ct);
}
```

The provider encapsulates the refresh flow, file locking, and credential store interaction. gRPC clients call `GetTokenAsync()` per request — it returns instantly when the cached token is valid.

### CLI Integration

```
repoql login               # Opens browser, completes OAuth, stores tokens
repoql login --device-code  # Device authorization flow (SSH, WSL, containers)
repoql logout               # Clears stored tokens
repoql whoami               # Shows current user, org, auth method
```

`repoql login` is the only auth command most users ever run.

**Containerized environments:** No browser, no OS credential store. Use `Cloud.ApiKey` config (existing) or mount a credential file. The design explicitly does not try to make `repoql login` work in containers.

### Infrastructure (Pulumi)

New resources:

| Resource | Purpose |
|----------|---------|
| Secret Manager `workos-api-key` | WorkOS API key for server-side SDK |
| Secret Manager `workos-client-id` | WorkOS client ID |

### Configuration

Cloud Run environment variables (via Secret Manager):

```
Auth__WorkOs__ApiKey=wos_...
Auth__WorkOs__ClientId=client_...
Auth__JwksUri=https://api.workos.com/sso/jwks/{clientId}
```

RepoQL host config — after `repoql login`, managed automatically:

```
Cloud.AuthToken=eyJhbG...          # access token (auto-refreshed)
Cloud.RefreshToken=<stored in OS credential manager>
```

Legacy `Cloud.ApiKey` setting continues to work during the transitional period.

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
| Unrecognized token format | `UNAUTHENTICATED` | "Unrecognized token format. Run: repoql login" |
| Expired JWT, refresh works | *(handled client-side, user never sees this)* | |
| Expired JWT, refresh fails | `UNAUTHENTICATED` | "Session expired. Run: repoql login" |
| Invalid JWT signature | `UNAUTHENTICATED` | "Invalid session. Run: repoql login" |
| Unknown API key | `UNAUTHENTICATED` | "API key not recognized. Run: repoql login" |
| Revoked API key | `PERMISSION_DENIED` | "API key has been revoked" |

Every error points the user to the one command that fixes it.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Sessions as primary | API keys as primary | Users shouldn't manage keys. Log in once, forget about it. |
| Device flow as first-class | Device flow as fallback | SSH, WSL, containers are common developer environments, not edge cases |
| JWKS public key validation | Per-request WorkOS API call | Local validation, zero latency, works during WorkOS outages |
| OS credential store | Flat file for refresh token | Refresh tokens are long-lived secrets — encrypt at rest |
| File lock for refresh | Token daemon | Simpler. Daemon is over-engineered for current scale |
| Fail-closed (`#if DEBUG`) | Runtime config flag | Compile-time guarantee — no-auth path can't exist in release binaries. Can't be misconfigured. |
| Portal for key management | CLI key management (`repoql keys`) | At current scale, portal is sufficient. CLI commands are premature |
| WorkOS for everything | Mixed providers | One identity provider, one set of user IDs, one org model |
| Ephemeral port for callback | Fixed port | No conflicts, no configuration |

## Alternatives Considered

**API keys as primary:** The Stripe/OpenAI model. But those are API-first products where the user IS a developer configuring integrations. RepoQL users are developers using a tool — they want to log in and forget, not manage keys.

**DIY OAuth2:** Build the login flow ourselves against Google/GitHub. Works, but then we also build session management, token storage, user management, org support. WorkOS does all of this with a free tier of 1M MAU.

**Refresh on 401 only (defer background refresh):** Simpler but the user experiences auth latency every 5 minutes. Unacceptable — the north star is invisible auth. Background refresh stays in Phase 1.

**Unkey for API keys:** Purpose-built, open source. But adds a second auth provider alongside WorkOS. If WorkOS API Keys prove insufficient, Unkey is the upgrade path.

## Pre-Implementation Verification

Before committing to WorkOS, verify:

| Question | Impact if no |
|----------|-------------|
| Does WorkOS support RFC 8628 Device Authorization Grant? | Must build DIY device flow (display URL + code, poll for completion) |
| Does `WorkOS.net` SDK cover OAuth2/PKCE code exchange + refresh? | Must write raw HTTP calls against WorkOS endpoints (standard OAuth2, but SDK barely helps) |
| WorkOS API Keys pricing? | May affect Phase 2 economics |

## Risks

| Risk | Mitigation |
|------|------------|
| WorkOS .NET SDK incomplete for OAuth2 | Standard OAuth2 endpoints — raw HTTP is well-understood. SDK is convenience, not dependency. |
| WorkOS outage blocks login | Existing sessions continue (refresh tokens are local). Only new logins blocked. |
| OS credential store unavailable | Fall back to encrypted file with machine-bound key. Warn user. |
| Refresh token stolen from disk | OS credential store encrypts at rest. Token is per-user, revocable from WorkOS dashboard. |
| Concurrent hosts race on refresh | File lock + re-read pattern. Worst case: one extra refresh call. |
| WorkOS doesn't support device flow | DIY device flow: generate code, host a verification page, poll. More work but well-understood pattern. |
| WSL browser launch unreliable | `--device-code` is the recommended path for WSL. Documented explicitly. |

## Extension Points

| Point | What it enables |
|-------|----------------|
| `AuthIdentity.OrgId` | Organization-based access control, usage quotas per org |
| WorkOS Organizations | Multi-tenant billing, team management |
| WorkOS SSO | Enterprise customers federate identity ($125/connection when justified) |
| WorkOS API Keys widget | Embeddable self-service key management in dashboard |

## Phasing

**Phase 1 — Login flow + JWT auth + silent refresh:**
- WorkOS integration for `repoql login` (browser + device code)
- JWKS warm-up at startup (`IHostedService`)
- JWT validation in interceptor (all handler types)
- `ICloudCredentialProvider` replacing static API key injection
- Silent token refresh with file locking for concurrent hosts
- OS credential store for refresh tokens (with encrypted file fallback)
- `repoql logout` and `repoql whoami`
- Existing `Cloud.ApiKey` continues to work (transitional, removing in < 7 days)
- Fail-closed auth (`#if DEBUG` bypass only — no-auth path excluded from release builds)

**Phase 2 — WorkOS API Keys + portal management:**
- API key creation via WorkOS portal/dashboard
- Synchronous Firestore write at key creation time (not webhook-dependent)
- Webhook endpoint for lifecycle events (revocation, expiry)
- Firestore cache with real-time listeners
- Remove legacy `rql_` key support
- Deprecate static `ApiKeyHashes` config

**Phase 3 — Organizations + billing:**
- WorkOS organizations for multi-tenancy
- Per-org usage quotas
- WorkOS API Keys widget in dashboard
- SSO for enterprise customers
