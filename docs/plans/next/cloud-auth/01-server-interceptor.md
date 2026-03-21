# Plan: Server-Side Auth Interceptor

Implements: [Cloud Auth Design — Interceptor Architecture](../../../designs/future/cloud-auth-design.md#interceptor-architecture)

## Scope

**Covers:**
- JWT validation via JWKS public key in all three cloud services (`Cloud.Service`, `Embedding.Service`, `Inference.Service`)
- Token format discrimination (JWT vs legacy `rql_` prefix)
- `AuthIdentity` record on call context
- `ServerStreamingServerHandler` override (currently missing)
- JWKS cache warm-up at startup
- Fail-closed auth (`#if DEBUG` bypass only)
- Unrecognized token format error

**Does not cover:**
- Client-side token refresh (Plan: 02 — Credential Provider)
- OAuth2 login flow (Plan: 03 — CLI Login)
- Firestore-backed API key lookup (Phase 2)
- Usage tracking / rate limiting (post-Phase 1)

## Enables

Once the interceptor validates JWTs:
- **Plan 02 (Credential Provider)** can send JWTs instead of static API keys — the server will accept them
- **Plan 03 (CLI Login)** has a server to authenticate against
- **Per-user identity** — every request carries a user ID, enabling future usage tracking and rate limiting
- **Legacy `rql_` keys continue working** — no migration pressure during rollout

## Prerequisites

- WorkOS account with project configured
- WorkOS client ID and JWKS URI known
- `Microsoft.IdentityModel.Tokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect` NuGet packages added to cloud service projects
- Pulumi secrets created for `workos-api-key` and `workos-client-id` (can be done in parallel)

## North Star

A JWT issued by WorkOS is validated locally in < 1ms with zero external calls. Legacy `rql_` keys work identically to today. No request ever passes through without auth in a release build.

## Done Criteria

### Shared Auth Library

- The `AuthInterceptor` shall be extracted to a shared project (or shared source) used by all three services
  - Today each service has its own copy of `ApiKeyAuthInterceptor` — consolidate
- The `AuthIdentity` record shall be defined in the shared auth project
- The `AuthMethod` enum shall include `Session` and `ApiKey` values

### Token Discrimination

- The interceptor shall detect JWT format (three dot-separated base64 segments) and route to JWKS validation
- The interceptor shall detect `rql_` prefix and route to SHA-256 hash lookup (existing behavior)
- When the token matches neither format, the interceptor shall return `UNAUTHENTICATED` with "Unrecognized token format. Run: repoql login"

### JWT Validation

- The interceptor shall validate JWT signature using WorkOS JWKS public keys
- The interceptor shall validate `exp` claim with 5-minute clock skew tolerance (`TokenValidationParameters.ClockSkew`)
- The interceptor shall extract `sub` claim as user ID
- The interceptor shall extract `org_id` claim if present (nullable)
- When JWT signature is invalid, return `UNAUTHENTICATED` with "Invalid session. Run: repoql login"
- When JWT is expired, return `UNAUTHENTICATED` with "Session expired. Run: repoql login"

### JWKS Cache

- The interceptor shall warm the JWKS cache at startup via `IHostedService`
  - `ConfigurationManager<OpenIdConnectConfiguration>` handles caching and rotation natively
- When JWKS fetch fails at startup, the service shall log a warning and continue (degrade to API-key-only auth)
- The JWKS cache shall rotate keys automatically per `ConfigurationManager` defaults

### Handler Coverage

- The interceptor shall override `UnaryServerHandler`
- The interceptor shall override `ServerStreamingServerHandler`
- The interceptor shall override `DuplexStreamingServerHandler`
- All three overrides shall call the same `Validate` method

### Auth Bypass

- In DEBUG builds (`#if DEBUG`), when no API key hashes are configured AND no JWKS URI is configured, the interceptor shall skip validation
- In RELEASE builds, the no-auth code path shall not exist (compile-time exclusion)
- When auth is bypassed in DEBUG, the interceptor shall log a prominent warning at startup

### AuthIdentity on Context

- When validation succeeds, the interceptor shall set an `AuthIdentity` on the call context
  - JWT path: `UserId` from `sub`, `Method = Session`, `DisplayName` from email claim or `sub`
  - API key path: `UserId` from a placeholder (no user mapping in Phase 1), `Method = ApiKey`, `DisplayName` from key hash prefix
- Downstream services shall be able to read `AuthIdentity` from `ServerCallContext` via extension method

## Constraints

- **No per-request WorkOS API calls** — JWT validation is local (JWKS public key). Design constraint: gRPC latency.
- **Three separate service deployments** — `Cloud.Service`, `Embedding.Service`, `Inference.Service` are independently deployed. Shared code must be a project reference or shared source, not a deployed service.
- **Legacy keys must keep working** — existing `Cloud.ApiKey` / `ApiKeyHashes` config path is unchanged. Design: transitional, removing in < 7 days.
- **`Microsoft.IdentityModel.Tokens`** — use the standard .NET JWT validation stack, not a custom implementation.

## References

- [Cloud Auth Design](../../../designs/future/cloud-auth-design.md) — full architecture
- [WorkOS JWKS endpoint](https://workos.com/docs/reference/sso/jwks) — public key source
- `src/RepoQL.Cloud.Service/Auth/ApiKeyAuthInterceptor.cs` — existing interceptor to extend
- `src/RepoQL.Embedding.Service/ApiKeyAuthInterceptor.cs` — duplicate to consolidate
- `src/RepoQL.Inference.Service/ApiKeyAuthInterceptor.cs` — duplicate to consolidate
- `Microsoft.IdentityModel.Tokens` — `ConfigurationManager<OpenIdConnectConfiguration>` for JWKS caching

## Error Policy

Auth errors are security boundaries — fail closed, never degrade silently:
1. Invalid/missing token → `UNAUTHENTICATED` with actionable message
2. JWKS fetch failure at startup → log warning, accept API keys only (degrade auth surface, don't disable it)
3. JWKS fetch failure during runtime → use cached keys (standard `ConfigurationManager` behavior)
4. Malformed JWT (can't parse) → treat as unrecognized format, not a JWT validation error
