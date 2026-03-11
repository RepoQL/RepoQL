# Authentication Platform Research

Research for selecting an authentication and API key management platform for RepoQL cloud services (embedding, inference).

*Research date: March 12, 2026*

## Context

RepoQL cloud services (`api.repoql.ai`) need authentication. Two primary auth methods are required:

1. **API keys** — long-lived, opaque tokens for programmatic access (the Stripe/OpenAI pattern)
2. **Refresh tokens** — session-based auth for interactive use (dashboard, CLI login)

The platform must support multi-tenancy (organizations), .NET/C# integration, and gRPC services. Free tier viability matters — RepoQL is pre-revenue.

---

## WorkOS

Identity and access management platform. Founded 2020, backed by a]z. Powers Vercel, Perplexity, Webflow auth.

### Core Capabilities

| Capability | Detail |
|------------|--------|
| User Management | Complete identity layer — email/password, social login, MFA, email verification |
| Organizations | Multi-tenant model with roles, permissions, org-scoped memberships |
| SSO | SAML/OIDC federation, $125/connection/month |
| AuthKit | Hosted auth UI with all methods, framework SDKs |
| Sessions | OAuth2 flow — access tokens (JWT, 5min default) + refresh tokens (30-day default, configurable) |
| API Keys | Launched October 2025 — embeddable widget for org-scoped keys with programmatic API |
| Free tier | 1M MAU, unlimited organizations |

> [WorkOS Docs](https://workos.com/docs) — product documentation
> [WorkOS Blog](https://workos.com/blog/api-keys) — API Keys feature announcement (October 2025)
> [WorkOS Pricing](https://workos.com/pricing) — free tier details

### Session / Refresh Token Flow

WorkOS implements standard OAuth2 with PKCE:

1. Client redirects to WorkOS authorization URL
2. WorkOS authenticates user, returns authorization code
3. Server exchanges code for access token (JWT) + refresh token
4. Access token used for requests (short-lived, 5min default)
5. Refresh token exchanges for new access token when expired
6. Session lifetime configurable per-environment

Access tokens are JWTs containing user ID, org ID, permissions. Refresh tokens are opaque strings stored server-side.

> [WorkOS Session Management](https://workos.com/docs/user-management/sessions) — session architecture
> [WorkOS Authentication Flow](https://workos.com/docs/user-management/authentication) — OAuth2 implementation

### API Keys Feature (October 2025)

WorkOS launched API Keys as a first-party feature:

- Org-scoped keys with configurable permissions
- Embeddable React widget for key management UI
- Programmatic API for creating, listing, revoking keys
- Keys hashed server-side (SHA-256 standard practice)
- Webhook events: `api_key.created`, `api_key.revoked`

This directly addresses RepoQL's primary auth method. Keys are scoped to organizations, matching RepoQL's multi-tenant model.

> [WorkOS API Keys Blog](https://workos.com/blog/api-keys) — feature announcement
> [WorkOS Changelog](https://workos.com/changelog) — API keys GA timeline

### .NET SDK

`WorkOS.net` v2.11.0 on NuGet. MIT license.

| Feature | SDK Support |
|---------|------------|
| User Management | Yes |
| SSO | Yes |
| Organizations | Yes |
| Directory Sync | Yes |
| Webhooks | Yes |
| Session/JWT verification | No (unlike Node.js SDK) |
| API Keys | Unknown — SDK may lag feature launch |

The .NET SDK covers core features but lacks session management helpers available in the Node.js SDK. JWT validation would need custom implementation using standard .NET JWT libraries (`Microsoft.IdentityModel.Tokens`).

> [NuGet: WorkOS.net](https://www.nuget.org/packages/WorkOS.net) — package listing
> [GitHub: workos/workos-dotnet](https://github.com/workos/workos-dotnet) — source repository

### gRPC Integration Pattern

No official gRPC examples exist. Standard pattern for gRPC + token auth:

```
Client → metadata["authorization"] = "Bearer <token>" → Server interceptor validates
```

For API keys: extract from metadata, hash with SHA-256, lookup in store.
For JWTs: extract from metadata, validate signature + claims using `Microsoft.IdentityModel.Tokens`.

> Field consensus from gRPC documentation and community patterns

### Rate Limiting

WorkOS rate limits its own API (6,000 req/60s) but does not provide rate limiting as a service for your endpoints. Rate limiting for RepoQL would need to be implemented separately.

---

## Alternatives

### Clerk

Auth platform with API keys in public beta (December 2025).

| Capability | Detail |
|------------|--------|
| API Keys | Public beta — user-scoped and org-scoped |
| Free tier | 10,000 MAU (vs WorkOS 1M) |
| .NET SDK | Community-maintained, not official |
| Multi-tenant | Yes — organizations with roles |

Clerk's API key feature is newer and in beta. The free tier is significantly smaller than WorkOS.

> [Clerk Docs](https://clerk.com/docs) — product documentation
> [Clerk API Keys](https://clerk.com/changelog/api-keys-public-beta) — beta announcement

### PropelAuth

Auth with native API key support (GA).

| Capability | Detail |
|------------|--------|
| API Keys | GA — configurable expiration, org-scoped |
| Free tier | 1,000 MAU |
| .NET SDK | Available |
| Multi-tenant | Yes — organizations, RBAC |

PropelAuth has the most mature API key feature but the smallest free tier.

> [PropelAuth Docs](https://docs.propelauth.com) — product documentation

### Unkey

Purpose-built API key management. Open source (AGPL-3.0).

| Capability | Detail |
|------------|--------|
| Purpose | API key management only — not an identity provider |
| Model | Pairs with any auth provider (WorkOS, Clerk, Auth0, custom) |
| Features | Rate limiting, usage analytics, key rotation, temporary keys |
| Pricing | Free tier available, usage-based |
| Self-host | Yes — open source |

Unkey is complementary, not competitive. It manages API keys while another provider handles identity. Could pair with WorkOS: WorkOS for identity + sessions, Unkey for API key management if WorkOS's built-in API keys prove insufficient.

> [Unkey](https://unkey.com) — product website
> [GitHub: unkeyed/unkey](https://github.com/unkeyed/unkey) — source repository

### Build It Yourself

The Stripe/OpenAI/Anthropic pattern: long-lived opaque API keys with SHA-256 hashing.

| Aspect | Detail |
|--------|--------|
| Key format | Prefix + random bytes (e.g., `rql_live_...`) |
| Storage | SHA-256 hash in Firestore, prefix for lookup |
| Rotation | User-initiated, old key grace period |
| Scoping | Org-scoped, permission bitmask |
| Complexity | Low for API keys alone, high if adding identity/SSO/MFA |

This is the simplest path for API-key-only auth. Every major API provider uses this pattern. It only becomes complex when you also need user identity, SSO, and session management.

> Industry standard — Stripe, OpenAI, Anthropic all use this pattern

---

## Comparison

| Dimension | WorkOS | Clerk | PropelAuth | Unkey | DIY |
|-----------|--------|-------|------------|-------|-----|
| API Keys | GA (Oct 2025) | Beta | GA | GA (standalone) | Build |
| Identity/SSO | Full | Full | Full | None | Build |
| Free tier (MAU) | 1M | 10K | 1K | N/A | N/A |
| .NET SDK | Official | Community | Official | REST only | N/A |
| gRPC examples | None | None | None | None | N/A |
| Refresh tokens | OAuth2/PKCE | OAuth2 | OAuth2 | None | Build |
| Multi-tenant | Yes | Yes | Yes | Yes (keys only) | Build |
| Rate limiting | No | No | No | Yes | Build |
| Open source | No | No | No | Yes (AGPL) | N/A |
| Lock-in risk | Medium | Medium | Medium | Low | None |

---

## Production API Auth Patterns

How major API providers handle authentication (relevant context for design):

| Provider | Key format | Storage | Refresh tokens | Sessions |
|----------|-----------|---------|----------------|----------|
| Stripe | `sk_live_...` | SHA-256 hash | No | Dashboard only |
| OpenAI | `sk-...` | SHA-256 hash | No | Dashboard only |
| Anthropic | `sk-ant-...` | SHA-256 hash | No | Dashboard only |

All three use long-lived opaque API keys for programmatic access. None use OAuth2 refresh tokens for API access — refresh tokens are only for interactive dashboard sessions.

This is the dominant pattern: API keys for machines, sessions for humans.

---

## Gaps

- **WorkOS API Keys .NET SDK support**: The API Keys feature launched October 2025. Whether the .NET SDK (`v2.11.0`) includes API key management endpoints is unverified — may require direct REST calls.
- **WorkOS API Keys pricing**: Whether API keys count toward the 1M MAU free tier or are priced separately is unclear from public documentation.
- **Clerk API keys stability**: Public beta as of December 2025. GA timeline unknown.
- **WorkOS gRPC interceptor patterns**: No .NET examples exist for WorkOS + gRPC. Would need custom implementation.
- **Unkey .NET SDK**: REST-only — no official .NET client. Would need HTTP client wrapper.
- **WorkOS webhook reliability**: No independent assessments of webhook delivery guarantees found.
- **Context7 docs contradicted actual APIs** in previous RepoQL experience (Voyage AI) — WorkOS SDK capabilities should be verified against actual SDK source before committing to an integration approach.

---

## Summary

| Platform | Best for | Risk |
|----------|----------|------|
| WorkOS | Full identity + API keys in one platform, massive free tier | .NET SDK may lag features, no gRPC patterns |
| Clerk | Teams already using Clerk | Small free tier, API keys still beta |
| PropelAuth | Mature API keys needed now | Smallest free tier (1K MAU) |
| Unkey | API key management alongside another identity provider | Not an identity provider — still need auth |
| DIY API keys | API-key-only auth, no identity needs | Grows complex if identity/SSO/MFA needed later |
| WorkOS + Unkey | Identity from WorkOS, sophisticated key management from Unkey | Two dependencies instead of one |
