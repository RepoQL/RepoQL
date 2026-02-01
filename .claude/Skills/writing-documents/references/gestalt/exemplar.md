# Identity Service — Gestalt

**What it is**: The JWT token issuer and permission resolver for the entire platform.

**Why it exists**: Centralized authentication eliminates duplicate login systems across 40+ services. Event-driven permissions enable real-time access control.

**Where it fits**: Foundation layer that every other service depends on for authentication and authorization.

---

## How It Fits

```
External IdPs (Auth0, Okta)
        ↓ credentials verified
Identity Service
        ↓ JWT with platform context
All 40+ Domain Services
        ↓ validate locally
Protected Resources
```

### Capsule: FoundationLayer

**Invariant**
Identity is one of three foundation services everything else depends on.

**Example**
Identity down = platform-wide outage. No new JWTs. Existing tokens valid ~1 hour.
//BOUNDARY: Foundation services (Identity, Tenant, Billing) are single points of failure.

**Depth**
- Auth0 down = end users can't login (staff unaffected)
- Okta down = staff can't login (end users unaffected)
- Identity down = nobody gets new tokens

---

## Key Concepts

### Authentication Pattern

External IdP (Auth0/Okta) verifies credentials, MFA, account recovery. Identity Service adds platform-specific context (tenant, roles, permissions). JWT issued by Identity, consistent format regardless of auth method.

Services validate tokens locally — no runtime call to Identity per request.

### The Vocabulary Problem

### Capsule: EntityConfusion

**Invariant**
"User", "Account", "Identity", and "Profile" mean different things but get conflated.

**Example**
Same human: Identity (auth), Account (billing), Profile (display name), User (legacy tables).
//BOUNDARY: One human = ONE Identity, MANY tenant memberships.

**Depth**
- Login, JWT, passwords → "Identity"
- Plan, billing, subscription → "Account"
- Name, avatar, preferences → "Profile"
- Legacy code still says "User" for all three

### Account Merging

**IRREVERSIBLE.** Once two identities merge, original identity keys are gone. Event history preserved but cannot "unmerge" without manual data surgery.

---

## Constraints

- **Event-sourced**: All state changes are events. No rollback.
- **Permission package lag**: Identity deploys new permissions; services consume via package asynchronously.
- **Multi-region required**: DR replication must be active.
- **JWT is the only auth mechanism**: No service-to-service calls without valid token.

---

## What It Does NOT Do

- Store profile/display data (Profile Service)
- Store tenant structure (Tenant Service)
- Process payments (Billing domain)
- Verify passwords/MFA directly (delegated to Auth0/Okta)

---

## Deeper

- [Domain Model](domain-model.md) — Identity entity, account types
- [Architecture](architecture.md) — Event sourcing, CQRS, storage
- [Authentication System](authentication-system.md) — OAuth, tokens, MFA
- [Account Merging](account-merging.md) — Merge process, constraints

---

## Why This Works as Gestalt

**Essential essence in first paragraph**: JWT authority, centralized auth, foundation layer.

**Key concepts unlock understanding**:
- Authentication pattern (IdP → Identity → JWT → Services)
- The Vocabulary Problem (entity confusion)
- Irreversibility (the big gotcha)

**Constraints shape decisions**: Event-sourced, permission lag, multi-region.

**What it does NOT do**: Prevents scope confusion.

**Pointers to depth**: Links to detail, doesn't duplicate it.

**After reading**: You understand what Identity is, how it fits, what to be careful about. You could reason about where auth changes belong, predict blast radius, explain the system to someone else.
