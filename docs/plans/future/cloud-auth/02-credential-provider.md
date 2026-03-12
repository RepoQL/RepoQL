# Plan: Client-Side Credential Provider

Implements: [Cloud Auth Design — Client-Side Credential Provider](../../../designs/future/cloud-auth-design.md#client-side-credential-provider), [Silent Token Refresh](../../../designs/future/cloud-auth-design.md#silent-token-refresh)

## Scope

**Covers:**
- `ICloudCredentialProvider` interface and implementation
- Access token caching and expiry detection
- Refresh token exchange with WorkOS
- File-lock coordination for concurrent hosts
- OS credential store integration (Windows Credential Manager, macOS Keychain, Linux Secret Service)
- Encrypted file fallback for headless Linux
- Integration with `GrpcEmbeddingProvider`, `InferenceClient`, and `FeedbackStore`
- `CloudSettings` config changes (`AuthToken`, `RefreshToken`)

**Does not cover:**
- The login flow that acquires tokens initially (Plan: 03 — CLI Login)
- Server-side JWT validation (Plan: 01 — Server Interceptor)
- API key management (Phase 2)

## Enables

Once the credential provider exists:
- **Plan 03 (CLI Login)** has somewhere to store tokens — login writes to credential store, provider reads from it
- **Existing gRPC clients work with JWTs** — `GrpcEmbeddingProvider` and `InferenceClient` get tokens from the provider instead of static config
- **Silent refresh** — users never see auth expiry during normal use
- **Multiple hosts** — all RepoQL hosts on a machine share one session, coordinated via file lock

## Prerequisites

- WorkOS account with OAuth2/PKCE application configured (refresh token endpoint known)
- Plan 01 (Server Interceptor) deployed or deployable — the provider needs a server that accepts JWTs
- OS credential store libraries identified:
  - Windows: `System.Security.Cryptography.ProtectedData` (DPAPI) or Windows Credential Manager via P/Invoke
  - macOS: `security` CLI or Keychain API
  - Linux: `libsecret` via D-Bus or encrypted file fallback

## North Star

A developer using RepoQL daily never re-authenticates. Token refresh is invisible — no latency, no errors, no user action. When something goes wrong, the error message tells them exactly what to do: `repoql login`.

## Done Criteria

### ICloudCredentialProvider

- The `ICloudCredentialProvider` interface shall expose `Task<string> GetTokenAsync(CancellationToken ct)`
- `GetTokenAsync` shall return a valid access token (JWT), refreshing if needed
- When no tokens are stored (user never logged in), `GetTokenAsync` shall throw with "Not authenticated. Run: repoql login"

### Access Token Caching

- The provider shall cache the current access token in memory
- When the cached token has > 30 seconds remaining, `GetTokenAsync` shall return it immediately (no I/O)
- When the cached token has < 30 seconds remaining, `GetTokenAsync` shall trigger a background refresh and return the current token
- When the cached token is expired, `GetTokenAsync` shall refresh synchronously before returning

### Token Refresh

- The provider shall exchange the refresh token for a new access token + new refresh token via WorkOS token endpoint
- When refresh succeeds, the provider shall persist the new refresh token to the credential store and the new access token to `~/.repoql/auth.json`
- When refresh fails with an invalid/revoked refresh token, the provider shall throw with "Session expired. Run: repoql login"
- When refresh fails with a network error, the provider shall retry once, then throw with a network-specific message

### Concurrent Host Coordination

- Before refreshing, the provider shall acquire a file lock on `~/.repoql/.auth-lock`
- After acquiring the lock, the provider shall re-read the access token from `~/.repoql/auth.json`
  - If the re-read token is valid (another host refreshed), release the lock and use it
  - If still expired, refresh, persist, release the lock
- The file lock shall use `FileStream` with `FileShare.None` and a timeout (5 seconds)
- When the lock cannot be acquired within timeout, the provider shall re-read the access token (the holder likely just refreshed)

### OS Credential Store

- On Windows, the provider shall store the refresh token in Windows Credential Manager (target: `repoql:refresh-token`)
- On macOS, the provider shall store the refresh token in Keychain (service: `repoql`, account: `refresh-token`)
- On Linux, the provider shall attempt `libsecret` via the Secret Service D-Bus API
- When the OS credential store is unavailable, the provider shall fall back to `~/.repoql/.credentials` encrypted with a machine-bound key
  - Linux: derived from `/etc/machine-id` via PBKDF2
  - Windows: DPAPI (automatic)
- The provider shall log a warning when falling back to encrypted file

### Access Token Disk Storage

- The access token shall be stored in `~/.repoql/auth.json` as `{ "accessToken": "...", "expiresAt": "ISO8601" }`
- The file shall be readable by the current user only (chmod 600 on Unix, ACL on Windows)
- The access token is short-lived (5 min) — disk storage is acceptable for cross-process sharing

### Client Integration

- `GrpcEmbeddingProvider` shall accept an `ICloudCredentialProvider` instead of a static `string apiKey`
  - `AuthHeaders()` shall call `GetTokenAsync()` instead of reading `_apiKey`
- `InferenceClient` shall accept an `ICloudCredentialProvider` instead of a static `string apiKey`
  - Same pattern as embedding provider
- `FeedbackStore.SubmitToCloudAsync` shall use `ICloudCredentialProvider` instead of reading `config.Cloud.ApiKey`
- When `ICloudCredentialProvider` is unavailable (no tokens, no API key), clients shall be disabled (existing `DisabledInferenceProvider` pattern)

### Backward Compatibility

- When `Cloud.ApiKey` is configured (legacy path), the provider shall use it as a static bearer token (no refresh)
- The DI registration shall prefer credential-provider auth over static API key when both are available
- When neither tokens nor API key are configured, cloud services shall be disabled (not errored)

## Constraints

- **Host never talks to WorkOS for validation** — only for refresh token exchange. Design constraint: laptop-first.
- **No WorkOS SDK dependency in the host** — refresh is a standard OAuth2 token endpoint call (`HttpClient` + form POST). The host must not depend on `WorkOS.net`.
- **File lock is best-effort** — a crashed host may leave a stale lock. Use `FileStream` with `FileOptions.DeleteOnClose` or a timeout-based recovery.
- **Refresh token never logged** — not in diagnostics, not in telemetry, not in error messages. Only the access token (short-lived) appears in gRPC metadata.

## References

- [Cloud Auth Design](../../../designs/future/cloud-auth-design.md) — token refresh flow, concurrent hosts section
- `src/RepoQL.Embedding.Client/GrpcEmbeddingProvider.cs` — current `_apiKey` + `AuthHeaders()` pattern to replace
- `src/RepoQL.Inference.Client/InferenceClient.cs` — current `_apiKey` pattern to replace
- `src/RepoQL.ConsoleApp/Feedback/FeedbackStore.cs` — current `config.Cloud.ApiKey` usage
- `src/RepoQL.ConsoleApp/Helpers/ServiceCollectionExtensions.cs:100-118` — DI registration for `IInferenceProvider`
- `src/RepoQL.Contracts/Configuration/RepoQlConfig.cs:153-158` — `CloudSettings` to extend
- [OAuth2 Token Endpoint](https://datatracker.ietf.org/doc/html/rfc6749#section-6) — refresh token grant

## Error Policy

Token errors guide the user back to recovery:
1. No stored tokens → "Not authenticated. Run: repoql login" (not an exception in the provider — return null/disabled state, let clients decide)
2. Refresh fails (revoked) → "Session expired. Run: repoql login"
3. Refresh fails (network) → retry once, then "Cannot reach authentication service. Check your connection."
4. Credential store unavailable → fall back to encrypted file, warn once at startup
5. File lock timeout → skip lock, re-read token, proceed (worst case: one redundant refresh)
