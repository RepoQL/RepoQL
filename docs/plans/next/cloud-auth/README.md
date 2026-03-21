# Cloud Auth Plans

Implements: [Cloud Auth Design](../../../designs/future/cloud-auth-design.md)

## Phase 1 — Login Flow + JWT Auth + Silent Refresh

| Plan | What it covers |
|------|---------------|
| [01 — Server Interceptor](01-server-interceptor.md) | JWT validation via JWKS, token discrimination, AuthIdentity, fail-closed, all handler types |
| [02 — Credential Provider](02-credential-provider.md) | `ICloudCredentialProvider`, token refresh, file locking, OS credential store, config integration |
| [03 — CLI Login](03-cli-login.md) | `repoql login` (browser + device code), `logout`, `whoami`, OAuth2/PKCE flow |

**Dependency order:** 01 can be deployed independently. 02 depends on WorkOS account setup. 03 depends on 02.

All three plans share a prerequisite: WorkOS account configured with a project, client ID, and redirect URIs.

## Phase 2 — WorkOS API Keys + Portal Management

Not yet planned. Depends on Phase 1 completion and WorkOS API Keys SDK evaluation.

## Phase 3 — Organizations + Billing

Not yet planned.
