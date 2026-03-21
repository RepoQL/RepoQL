# Plan: CLI Login Commands

Implements: [Cloud Auth Design — The Login Flow](../../../designs/future/cloud-auth-design.md#the-login-flow), [CLI Integration](../../../designs/future/cloud-auth-design.md#cli-integration)

## Scope

**Covers:**
- `repoql login` — browser-based OAuth2/PKCE flow with localhost callback
- `repoql login --device-code` — device authorization flow for SSH/WSL/containers
- `repoql logout` — clear stored tokens
- `repoql whoami` — display current user, auth method
- Localhost HTTP listener for OAuth callback
- PKCE code verifier/challenge generation

**Does not cover:**
- Token refresh after login (Plan: 02 — Credential Provider)
- Server-side JWT validation (Plan: 01 — Server Interceptor)
- API key management (Phase 2 — portal only)

## Enables

Once login exists:
- **Users can authenticate** — the entry point for session-based auth
- **Credential provider has tokens** — login writes refresh + access tokens that Plan 02 reads
- **`repoql whoami`** provides auth status for debugging
- **Device flow** covers SSH, WSL, containers, and headless servers

## Prerequisites

- Plan 02 (Credential Provider) implemented — login needs to store tokens via `ICloudCredentialProvider` (or its storage layer directly)
- WorkOS AuthKit application configured:
  - Client ID known
  - Redirect URI pattern registered: `http://localhost:*/callback` (wildcard port)
  - Device flow enabled (if WorkOS supports it — see Pre-Implementation Verification)
- WorkOS authorization endpoint and token endpoint URIs known

## North Star

`repoql login` works on the first try, every time, on every platform. Browser opens, user clicks, done. On SSH, `--device-code` is equally seamless. If anything goes wrong, the error message is the fix.

## Done Criteria

### repoql login (Browser Flow)

- The command shall generate a PKCE code verifier (cryptographically random, 43-128 chars) and code challenge (SHA256, base64url)
- The command shall start a localhost HTTP listener on an ephemeral port (OS-assigned, port 0)
- The command shall construct the WorkOS authorization URL with: `client_id`, `redirect_uri` (including ephemeral port), `response_type=code`, `code_challenge`, `code_challenge_method=S256`, `state` (CSRF token)
- The command shall open the authorization URL in the default browser
  - On failure to open browser, display the URL and instruct the user to open it manually
- The localhost listener shall wait for the OAuth callback with `code` and `state` parameters
  - When `state` doesn't match, reject with "Authentication failed: invalid state. Try again."
  - When callback has an `error` parameter, display the error description
- The command shall exchange the authorization code for tokens via WorkOS token endpoint (POST with `grant_type=authorization_code`, `code`, `code_verifier`, `client_id`, `redirect_uri`)
- The command shall store the refresh token in the OS credential store (via Plan 02's storage layer)
- The command shall store the access token in `~/.repoql/auth.json`
- The command shall display "Logged in as {email}" on success
- The localhost listener shall respond with a simple HTML page: "Authentication successful. You can close this tab."
- The localhost listener shall shut down after receiving the callback (or after 120 seconds timeout)

### repoql login --device-code

- The command shall request a device code from WorkOS (POST to device authorization endpoint with `client_id`)
- The command shall display: "To authenticate, visit: {verification_uri}" and "Enter code: {user_code}"
- The command shall poll the token endpoint at the interval specified by WorkOS (typically 5 seconds)
  - While polling, display a spinner or countdown
  - When user completes authentication, receive tokens and store them (same as browser flow)
  - When code expires (typically 15 minutes), display "Code expired. Run: repoql login --device-code" to try again
- If WorkOS does not support device flow: implement a DIY alternative
  - Generate a short code, display URL to a hosted verification page
  - Poll a RepoQL endpoint that receives the callback
  - This is a separate design decision — flag it during implementation

### repoql logout

- The command shall clear the refresh token from the OS credential store
- The command shall delete `~/.repoql/auth.json`
- The command shall display "Logged out"
- When no tokens are stored, display "Not logged in" (not an error)

### repoql whoami

- The command shall read the current access token (via credential provider)
- The command shall decode the JWT payload (without validation — this is local display, not auth)
- The command shall display: user email, user ID, organization (if any), auth method (session or API key), token expiry
- When using a legacy API key, display "Authenticated via API key (hash prefix: {first 8 chars})"
- When not authenticated, display "Not logged in. Run: repoql login"

### Command Registration

- All commands shall be registered via the existing command system (`[CommandClass]` + `[Command("name")]`)
- `login`, `logout`, `whoami` shall be top-level commands (not subcommands of `auth`)
- Commands shall be available in both CLI and MCP modes

## Constraints

- **No WorkOS SDK in the host binary** — OAuth2 is standard HTTP. Use `HttpClient` for token endpoint calls. PKCE is `SHA256` + `Base64Url` — no library needed.
- **Ephemeral port only** — never bind to a fixed port. The redirect URI includes the port, so it's unique per login attempt.
- **Browser launch is best-effort** — `Process.Start` with URL. If it fails (headless, WSL, weird desktop), display the URL for manual copy-paste. This is not an error.
- **WSL guidance** — when running under WSL (detected via `/proc/version` containing `microsoft` or `WSL`), suggest `--device-code` instead of browser flow.

## References

- [Cloud Auth Design](../../../designs/future/cloud-auth-design.md) — login flow, device flow, CLI integration
- [OAuth2 Authorization Code with PKCE](https://datatracker.ietf.org/doc/html/rfc7636) — PKCE spec
- [OAuth2 Device Authorization Grant](https://datatracker.ietf.org/doc/html/rfc8628) — device flow spec
- `src/RepoQL.ConsoleApp/CommandImplementations/` — existing command pattern
- `src/RepoQL.Commands/` — command framework (`[CommandClass]`, `[Command]`)
- WorkOS AuthKit documentation — authorization endpoint, token endpoint, JWKS URI
- Plan 02 (Credential Provider) — token storage interface

## Error Policy

Login errors must be instantly recoverable:
1. Browser fails to open → display URL for manual navigation (not an error)
2. User cancels in browser → "Authentication cancelled. Run: repoql login to try again"
3. Network error during code exchange → "Cannot reach authentication service. Check your connection and try again"
4. WorkOS returns error → display the error description from WorkOS (they're user-friendly)
5. Localhost listener timeout (120s) → "Authentication timed out. Run: repoql login to try again"
6. Device code expired → "Code expired. Run: repoql login --device-code to try again"
