---
description: Auth provider research for RepoQL cloud services — Clerk and alternatives for GitHub/Microsoft/Google social login.
tags: [auth, clerk, oauth, cloud-cache, identity]
audience: { human: 60, agent: 40 }
purpose: { research: 90, design: 10 }
---

# Auth Provider Research — Clerk and Alternatives

Research for selecting an authentication provider for RepoQL's cloud services (embedding cache, explain, reranking). Specifically: GitHub, Microsoft, and Google social login for developer-facing CLI + cloud backend.

*Research date: March 9, 2026*

## Context

RepoQL's cloud services need user authentication. The existing auth design ([auth-and-billing.md](../flows/future/llm-service/auth-and-billing.md)) specifies:

- **Identity providers:** GitHub (primary), Google, Apple (for Stripe path)
- **Credential types:** JWT refresh tokens (device flow OAuth) and operator-issued API keys
- **CLI auth:** `::login` / `repoql login` using device authorization grant (RFC 8628)
- **Backend:** .NET/C#, GCP Cloud Run
- **Scale:** < 10K users initially

This research evaluates Clerk against alternatives, with emphasis on three questions:

1. Can the provider handle GitHub, Microsoft, and Google social login?
2. Does it support device flow for CLI tools?
3. Can the backend access OAuth provider refresh tokens if needed?

The existing design mentions GitHub, Google, and Apple. This research adds Microsoft to the evaluation (many enterprise developers use Microsoft identity) and explores whether refresh tokens from the identity provider are actually needed.

---

## Do We Need Provider Refresh Tokens?

Before evaluating providers, a foundational question: does RepoQL need the OAuth provider's refresh token (GitHub's, Google's, Microsoft's), or just its own session management?

**If the goal is identity only** (who is this user? are they entitled?): provider refresh tokens are not needed. Any auth provider creates its own session after the initial OAuth handshake. The provider's token is consumed once during login, then the auth provider's session takes over.

**When you need the provider's access token:**
- Calling the provider's API on behalf of the user (listing GitHub repos, reading Google Drive, etc.)
- Any operation requiring the user's authorization at the third-party service

**RepoQL's cloud services don't call GitHub/Google/Microsoft APIs on behalf of users.** The embedding cache, explain, and reranking services need to know *who* the user is and *what plan* they're on. They never need to act as the user at GitHub.

| Provider | Token Expiry | Refresh Token? | Needed for Identity Only? |
|----------|-------------|----------------|--------------------------|
| GitHub (OAuth App) | Never (revoked after 1yr unused) | No — OAuth Apps don't issue them | No |
| Microsoft | ~1hr access / 90-day sliding refresh | Yes (requires `offline_access` scope) | No |
| Google | 1hr access / long-lived refresh | Yes (requires `access_type=offline`) | No |

> [GitHub token expiration docs](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/token-expiration-and-revocation) — OAuth App tokens don't expire
> [Microsoft refresh tokens](https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens) — 90-day sliding window
> [Google OAuth server flow](https://developers.google.com/identity/protocols/oauth2/web-server) — refresh token conditional on `access_type=offline`

**Implication:** Provider refresh tokens are a non-requirement for RepoQL. This simplifies the evaluation — the provider needs to handle OAuth login and maintain its own session. RepoQL's backend validates a JWT and resolves it to an account.

---

## Clerk

Released 2021. YC-backed. Focused on authentication UI components for web apps.

### Capabilities

| Dimension | Value |
|-----------|-------|
| GitHub OAuth | Supported — shared dev credentials, custom for production |
| Microsoft OAuth | Supported (consumer). SAML/EASIE for enterprise Entra |
| Google OAuth | Supported |
| Other providers | 20+ social providers on all tiers |
| Multi-tenant | First-class "Organizations" feature with RBAC |
| JWT verification | Stateless — PEM public key or JWKS, standard RS256 |

> [Clerk social connections](https://clerk.com/docs/guides/configure/auth-strategies/social-connections/overview) — provider list
> [Clerk JWT verification](https://clerk.com/docs/guides/sessions/manual-jwt-verification) — networkless validation

### CLI / Device Flow

**Not supported.** Clerk is browser-first. No device authorization flow (RFC 8628). No OAuth client credentials flow.

Available workarounds:
- **API keys** (public beta, December 2025): users create keys in a web UI, use them in CLI. Verifiable via Clerk SDKs
- **M2M tokens** (GA October 2025, JWT format February 2026): service-to-service, not user-facing
- **Browser-based initial login** + cached long-lived JWT: requires a browser at least once

The absence of device flow is a significant gap for a CLI tool. The existing auth design specifies device flow as the primary login mechanism.

> [Clerk machine auth overview](https://clerk.com/docs/guides/development/machine-auth/overview) — API keys, M2M tokens
> [Clerk API keys beta](https://clerk.com/changelog/2025-12-11-api-keys-public-beta) — December 2025

### .NET SDK

Two options:

| SDK | Version | Status | Notes |
|-----|---------|--------|-------|
| `Clerk.BackendAPI` (official) | 0.6.2 | Beta | Auto-generated (Speakeasy). `AuthenticateRequestAsync()`. No ASP.NET Core middleware |
| `Clerk.Net` (community, Hawxy) | 1.15.0 | Mature | .NET 6+, .NET Framework 4.7.2+. ASP.NET Core middleware via `Clerk.Net.AspNetCore.Security`. Native AOT compatible |

Alternative: use Clerk as a standard OIDC provider with ASP.NET Core's built-in `AddOpenIdConnect()`.

> [Clerk C# SDK](https://github.com/clerk/clerk-sdk-csharp) — official, beta
> [Clerk.Net](https://github.com/Hawxy/Clerk.Net) — community, more mature
> [Alex Duggleby blog](https://alexduggleby.com/blog/using-clerk-as-an-oauth-provider-for-asp-net-core/) — standard OIDC approach

### Provider Token Access

Clerk stores provider OAuth tokens server-side. `getUserOauthAccessToken()` (backend-only) retrieves the provider's access token on demand. Clerk refreshes tokens lazily when requested, using stored refresh tokens.

Whether Clerk exposes the raw provider refresh token or only manages it internally is ambiguous in the documentation. Community discussions suggest Clerk holds the refresh token internally and returns refreshed access tokens.

This is moot for RepoQL — provider tokens aren't needed (see above).

> [getUserOauthAccessToken()](https://clerk.com/docs/reference/backend/user/get-user-oauth-access-token) — backend API

### Pricing

| Tier | Cost | MAU Limit | Notes |
|------|------|-----------|-------|
| Free | $0 | 10,000 MAU | All social connections. Session duration fixed at 7 days |
| Pro | $25/mo + $0.02/MAU over 10K | 50,000 MAU before forced upgrade | Customizable session duration |

> [Clerk pricing](https://clerk.com/pricing) — current plans
> [February 2026 pricing update](https://clerk.com/changelog/2026-02-05-new-plans-more-value) — "more affordable" changes

### Field Sentiment

Widely adopted in the Next.js/React ecosystem. Strong UI component library. Less common in .NET or CLI-oriented tooling. Community discussions note pricing concerns at scale and the browser-first orientation.

---

## WorkOS

Founded 2020. Enterprise SSO focus, expanded to general user management via AuthKit.

### Capabilities

| Dimension | Value |
|-----------|-------|
| GitHub OAuth | Supported (`GitHubOAuth`) |
| Microsoft OAuth | Supported (`MicrosoftOAuth`). Provider token passthrough documented |
| Google OAuth | Supported (`GoogleOAuth`) |
| CLI / Device flow | First-class. AuthKit includes CLI Auth based on RFC 8628 |
| Provider refresh tokens | Yes — returned in `Authenticate with code` response when enabled |
| .NET SDK | Official `WorkOS.net` v2.9.0, .NET Standard 2.0 |
| Multi-tenant | Organizations with RBAC |

> [WorkOS social login](https://workos.com/docs/user-management/social-login) — provider support
> [WorkOS CLI auth](https://workos.com/docs/authkit/cli-auth) — device flow
> [WorkOS.net NuGet](https://www.nuget.org/packages/WorkOS.net) — .NET SDK

### Pricing

| Tier | Cost | Notes |
|------|------|-------|
| User Management | Free up to 1M MAU | Social login, device flow, organizations |
| Enterprise SSO | $125/connection/month | Only if needed — not relevant for social login |

For RepoQL's use case (social login, < 10K users), the cost is $0.

> [WorkOS pricing](https://workos.com/pricing) — free for user management

### Field Sentiment

Positioned for developer tools and B2B SaaS. CLI auth is a differentiator. Less UI component library than Clerk (AuthKit is more minimal). The pricing model (free auth, expensive SSO) is attractive for tools that don't need enterprise SSO initially.

---

## Auth0

Okta-owned. The incumbent managed auth provider.

### Capabilities

| Dimension | Value |
|-----------|-------|
| GitHub OAuth | Supported |
| Microsoft OAuth | Supported |
| Google OAuth | Supported |
| CLI / Device flow | First-class, well-documented with .NET quickstart |
| Provider refresh tokens | Yes — stored on user profile, retrievable via Management API |
| .NET SDK | Mature — `Auth0.AuthenticationApi` v7.44, `Auth0.ManagementApi` v7.45 |
| Multi-tenant | Organizations feature |

> [Auth0 device flow](https://auth0.com/docs/get-started/authentication-and-authorization-flow/device-authorization-flow) — CLI auth
> [Auth0 identity provider tokens](https://auth0.com/docs/manage-users/user-accounts/user-account-linking/access-original-identity-provider-tokens) — provider token access

### Pricing

| Tier | Cost | Limitation |
|------|------|------------|
| Free | $0 | 25,000 MAU but **only 2 social connections** |
| B2C Essentials | $35/mo | 500 MAU base. Unlimited social connections |

The free tier's 2-connection limit blocks the use case (GitHub + Microsoft + Google = 3). The paid tier starts at $35/mo for only 500 MAU.

> [Auth0 pricing](https://auth0.com/pricing) — plan comparison

### Field Sentiment

Most mature managed auth. Extensive documentation. Pricing perceived as expensive compared to newer alternatives. Okta acquisition introduced concerns about enterprise-oriented pricing trajectory.

---

## Keycloak (Self-Hosted)

Open-source (Apache 2.0). Red Hat-backed. The reference implementation for self-hosted auth.

### Capabilities

| Dimension | Value |
|-----------|-------|
| GitHub OAuth | Supported as identity broker |
| Microsoft OAuth | Supported as identity broker |
| Google OAuth | Supported as identity broker |
| CLI / Device flow | Supported natively |
| Provider refresh tokens | Yes — via RFC 8693 token exchange (Keycloak 26.2+) |
| .NET SDK | No official SDK. Standard OIDC middleware works. Community `NETCore.Keycloak`. .NET Aspire integration exists |
| Multi-tenant | Realms |
| Cost | Free. Infrastructure costs only |

> [Keycloak identity brokering](https://www.keycloak.org/docs/latest/server_admin/#_identity_broker) — social providers
> [Keycloak device flow](https://www.keycloak.org/docs/latest/securing_apps/#_device_auth_grant) — CLI auth

### Operational Burden

Production deployment requires managing SSL, PostgreSQL, clustering, upgrades, and security patches. Significant ongoing operational cost for a small team. The power-to-complexity ratio favors managed services at < 10K users.

---

## Other Providers Evaluated

### Firebase Auth / Google Identity Platform

**Eliminated.** Firebase discards the OAuth provider's refresh token after sign-in. While this doesn't matter for identity-only use, Firebase also lacks device flow support and its .NET Admin SDK doesn't drive OAuth flows.

> [firebase-js-sdk #2532](https://github.com/firebase/firebase-js-sdk/issues/2532) — provider refresh token not stored

### Supabase Auth (GoTrue)

Provider tokens returned in callback but not stored — your app must capture and manage them. No CLI device flow. Community .NET SDK only. Self-hosting via GoTrue is an option.

### Microsoft Entra External ID

**Eliminated for this use case.** GitHub is not a supported social login provider. GitHub uses plain OAuth 2.0, not OIDC, making custom federation non-trivial. First-class .NET SDK (MSAL) is a strength, but the missing GitHub support is a blocker.

> [Entra External ID social providers](https://learn.microsoft.com/en-us/entra/external-id/customers/concept-supported-features-customers) — Google and Facebook only for social

### Descope

GitHub, Microsoft, Google supported. Strong provider token management via Management API. Official .NET SDK. No-code flow builder. Free for 7,500 MAU. **Device flow for CLI not documented** — worth investigating but couldn't confirm.

> [Descope provider tokens](https://docs.descope.com/manage/outboundapp/) — managed token access

---

## Raw OAuth (No Provider)

Implement OAuth directly against GitHub, Google, and Microsoft APIs. All three support device flow.

| Aspect | Assessment |
|--------|-----------|
| GitHub device flow | Supported. `POST /login/device/code` → poll for token |
| Google device flow | Supported. `POST /o/oauth2/device/code` → poll for token |
| Microsoft device flow | Supported. `POST /{tenant}/oauth2/v2.0/devicecode` → poll for token |
| Implementation scope | ~500-1000 lines for device flow across 3 providers. Token storage and refresh: ~200-300 lines |
| .NET SDK | ASP.NET Core authentication middleware built-in. `HttpClient` for token operations |
| Cost | $0 |
| Risk | Must get security right (PKCE, token encryption, etc.). No managed dashboard |

The existing auth design already envisions JWT issuance and validation as a backend concern. Raw OAuth for the login handshake is viable if the backend issues its own JWTs after validating the provider token.

---

## What CLI Tools Actually Ship

| Tool | Primary Auth | Fallback | Token Storage |
|------|-------------|----------|---------------|
| GitHub CLI | Device flow | PAT via `--with-token` | OS credential store |
| Azure CLI | Auth code + WAM (Win) | Device code | OS credential store |
| gcloud | Auth code + localhost | Bootstrap command for remote | File in home dir |
| Terraform | Auth code + PKCE | Manual token in config | Plaintext config |
| Pulumi | Browser OAuth | API token paste, OIDC token | Credential store |
| Vercel CLI | Device flow | — | File |
| Claude Code | API key env var | Interactive `/login` | Env var |

> [GitHub CLI auth](https://cli.github.com/manual/gh_auth_login) — device flow reference implementation
> [Vercel device flow](https://vercel.com/changelog/new-vercel-cli-login-flow) — "more secure and intuitive"

The industry has converged on two patterns: (a) authorization code + PKCE with localhost redirect for environments with browsers, and (b) device flow for headless/SSH/remote. Mature tools support both and auto-detect. OAuth 2.1 makes PKCE mandatory for all public clients.

---

## B2B Seat Management: Company Signup with Centralized Billing

A key scaling scenario: a company signs up for a Team plan, an admin assigns seats to employees on their domain, and billing is centralized to the company.

### Organization Creation

**Clerk:** Pre-built `<CreateOrganization />` and `<OrganizationSwitcher />` components. With "Membership required" mode (default since August 2025), users are prompted to create or join an org before accessing the app. Single flow: sign up → create org → start inviting.

**WorkOS:** API-driven. Your app checks for `org_id` in the access token after sign-in; if absent, you present a form that calls `workos.organizations.create()`. No turnkey org-creation widget — you build the form, WorkOS provides the backend.

> [Clerk Organizations Overview](https://clerk.com/docs/guides/organizations/overview) — prebuilt components
> [WorkOS Users and Organizations](https://workos.com/docs/authkit/users-organizations) — API-driven creation

### Seat Assignment and Member Invitations

**Clerk:** `<OrganizationProfile />` component provides tabs for Members, Invitations, and Requests. Admins invite by email with role assignment. No custom UI required unless desired.

**WorkOS:** `<UsersManagement />` React widget (Radix-based) lets org admins invite, remove, and change roles. Embeddable in your app. Invitations also available via API.

> [Clerk Invitations](https://clerk.com/docs/guides/organizations/add-members/invitations) — prebuilt UI + API
> [WorkOS User Management Widget](https://workos.com/docs/widgets/user-management) — embeddable widget

### Seat Limit Enforcement

**Clerk:** Built-in `max_allowed_memberships` property per organization. Default is 5. Set via API when creating/updating an org. **Clerk enforces natively** — rejects invitations that exceed the limit. Set different limits per org to match billing plan.

**WorkOS:** **No built-in seat cap.** If you need "this org has 10 seats," you enforce it in your application code before creating memberships. ~10 lines of code.

> [Clerk Organization Configuration](https://clerk.com/docs/guides/organizations/configure) — `max_allowed_memberships`

### Domain-Based Auto-Join

**Clerk:** Verified Domains feature. Admin adds a domain (e.g., `company.com`), verifies via DNS or email. Two modes: automatic invitation (users auto-join on signup) or automatic suggestion (admin approves). One org per domain.

**WorkOS:** Domain verification (DNS-based, self-serve via Admin Portal) + JIT provisioning. Matching email domains auto-join. Also works with SSO — users authenticating via SAML/OIDC with a verified domain are auto-added. One org per domain.

> [Clerk Verified Domains](https://clerk.com/docs/guides/organizations/add-members/verified-domains) — domain enrollment
> [WorkOS Domain Verification](https://workos.com/docs/authkit/domain-verification) — DNS + JIT

### Centralized Billing (Stripe Integration)

**Clerk:** **Clerk Billing** is a built-in product wrapping Stripe. Provides `<PricingTable />` component, plan selection, and entitlement data in the session via `has()` helper. **Per-seat variable pricing is "coming soon"** — flat-rate plans work today. 0.7% transaction fee on top of Stripe's fees.

**WorkOS:** **No built-in billing product.** Two Stripe add-ons:
- **Stripe Seat Sync**: Automatically reports active member counts to Stripe Meters whenever memberships change. Zero code after setup.
- **Stripe Entitlements**: Syncs Stripe subscription entitlements into JWT `entitlements` claim — feature gating without API calls.

Link org → Stripe via `stripeCustomerId` on the org object. Billing UI (checkout, portal) uses Stripe's own tools directly.

> [Clerk Billing](https://clerk.com/docs/guides/billing/overview) — built-in billing product
> [WorkOS Stripe Add-on](https://workos.com/docs/authkit/add-ons/stripe) — Seat Sync + Entitlements

### Seat Removal

**Clerk:** Hard delete only. `deleteOrganizationMembership()` — immediate removal, freed seat available instantly.

**WorkOS:** Two options: soft deactivation (sets membership to `inactive`, preserves data, revokes sessions, can reactivate later) or hard deletion. If Stripe Seat Sync is enabled, decremented count auto-reports to Stripe.

> [WorkOS Deactivate Memberships](https://workos.com/changelog/deactivate-organization-memberships-in-user-management) — soft delete option

### Enterprise Self-Service (Admin Portal)

**Clerk:** No equivalent of an IT admin portal. Org management is through embedded components (`<OrganizationProfile />`). SAML SSO supported but no self-serve setup. **SCIM directory sync not shipped** (on roadmap, investment announced February 2026). No audit logs.

**WorkOS:** Dedicated Admin Portal — hosted UI for IT admins with step-by-step IdP-specific walkthroughs. Org IT admins can self-serve SSO setup (SAML + OIDC), SCIM directory sync configuration, domain verification, certificate renewal, and audit log stream setup. No developer intervention needed.

> [WorkOS Admin Portal](https://workos.com/docs/admin-portal) — self-serve enterprise onboarding
> [Clerk roadmap](https://feedback.clerk.com/roadmap) — SCIM as future work

### Seat Management Comparison

| Dimension | Clerk | WorkOS |
|-----------|-------|--------|
| Org creation UX | Pre-built component, one flow | You build the form, API-driven |
| Member invite UI | Pre-built `<OrganizationProfile />` | `<UsersManagement />` widget |
| Seat cap enforcement | **Native** — `max_allowed_memberships` | **You build it** (~10 lines) |
| Seat count → Stripe | Per-seat pricing "coming soon" | **Stripe Seat Sync** — automatic |
| Entitlements in JWT | Plan/features via Clerk Billing | Stripe Entitlements synced to token |
| Domain auto-join | Verified Domains | Domain policy + JIT provisioning |
| Seat removal | Hard delete only | Soft deactivate or hard delete |
| SSO self-service | No admin portal | **Admin Portal** — self-serve |
| SCIM directory sync | Not shipped | **Shipped** — self-serve setup |
| Audit logs | Not shipped | Shipped (developer-emitted events) |
| Org data in JWT | `org_id`, `org_role`, `org_permissions` | `org_id`, `role`, `permissions`, `entitlements` |
| CLI device flow | **No** | **Yes** |

The tension: Clerk gives you seat *enforcement* natively but can't do seat-based *billing* yet. WorkOS gives you seat-based billing automation (Stripe Seat Sync) but makes you enforce the cap yourself. Both require Stripe for actual payments.

---

## Comparison

| Dimension | Clerk | WorkOS | Auth0 | Keycloak | Raw OAuth |
|-----------|-------|--------|-------|----------|-----------|
| GitHub + MS + Google | Yes | Yes | Yes (paid for 3+) | Yes | Yes |
| CLI device flow | **No** | Yes | Yes | Yes | DIY |
| Provider refresh tokens | Managed internally | Yes (raw) | Yes (raw) | Yes (exchange) | Full control |
| .NET SDK | Beta official + mature community | Official | Official, mature | Standard OIDC | Built-in |
| Free tier (< 10K) | 10K MAU | **1M MAU** | 25K MAU (2 connections) | Free (OSS) | Free |
| Cost at 10K users | $0 | $0 | $35+/mo | Infra only | $0 |
| Self-hosted | No | No | No | Yes | N/A |
| JWT verification | Stateless | Stateless | Stateless | Stateless | You build it |
| Multi-tenant | Organizations | Organizations | Organizations | Realms | You build it |
| CLI-oriented | **No** — browser-first | **Yes** | Yes | Yes | You decide |
| Operational burden | None | None | None | High | Low (bounded) |

---

## Gaps

- **Clerk device flow roadmap:** Could not determine if or when Clerk plans to support RFC 8628. The [Clerk feedback board](https://feedback.clerk.com/) may have a request — not checked directly
- **WorkOS provider token details for GitHub specifically:** Confirmed for Microsoft; likely same pattern for GitHub/Google but GitHub-specific documentation not found
- **Descope device flow:** May exist but isn't prominently documented. Worth asking directly
- **Clerk's exact refresh token behavior:** Whether `getUserOauthAccessToken()` returns the raw provider refresh token or only manages it internally is ambiguous
- **Auth0 pricing at 10K MAU:** The per-MAU cost beyond 500 on the Essentials plan was not precisely confirmed
- **OpenIddict:** Open-source .NET OAuth server that could run alongside the GCP backend, handling device flow + token exchange natively in C#. Not evaluated in this research — a potential alternative to managed auth SaaS
- **Logto, SuperTokens, Ory, FusionAuth:** Open-source alternatives with varying self-hosted and CLI support. Not evaluated

---

## Summary

Clerk is strong for web-first applications with pre-built UI components, generous free tier, and fast setup. Its absence of device flow support is a significant gap for RepoQL's CLI-first use case, where `::login` / `repoql login` is the primary authentication path.

WorkOS matches RepoQL's requirements most closely among managed providers: free at scale, explicit CLI device flow support, provider token passthrough, official .NET SDK, and positioning for developer tools. The pricing model (free auth, paid enterprise SSO) aligns with RepoQL's trajectory.

Auth0 has the most mature device flow implementation but is the most expensive option for three social providers. Keycloak is the most flexible but carries the highest operational burden. Raw OAuth is viable and bounded in scope — the existing auth design already handles JWT issuance and validation, so the managed provider's role is limited to the initial OAuth handshake.

The finding that provider refresh tokens are unnecessary for RepoQL simplifies the decision. Any provider that handles social login and device flow is sufficient — the backend issues its own JWTs and manages sessions independently.
