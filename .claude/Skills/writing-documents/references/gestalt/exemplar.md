# Identity Service — Gestalt

**What it is**: The JWT token issuer and permission resolver for the entire PushPay platform.

**Why it exists**: Centralized authentication eliminates duplicate login systems across 40+ services. Event-driven permissions enable real-time access control.

**Where it fits**: Foundation layer that every other service depends on for authentication and authorization.

---

## How It Fits

```
External IdPs (Auth0, Okta)
        ↓ credentials verified
Identity Service
        ↓ JWT with PushPay context
All 40+ Domain Services
        ↓ validate locally
Protected Resources
```

### Capsule: FoundationLayer

**Invariant**
Identity is one of three foundation services everything else depends on.

**Example**
Identity down = platform-wide outage. No new JWTs. Existing tokens valid ~1 hour.
//BOUNDARY: Foundation services (Identity, Organization, Community) are single points of failure.

**Depth**
- Auth0 down = end users can't login (staff unaffected)
- Okta down = staff can't login (end users unaffected)
- Identity down = nobody gets new tokens

---

## Key Concepts

### Authentication Pattern

External IdP (Auth0/Okta) verifies credentials, MFA, account recovery. Identity Service adds PushPay-specific context (organization, roles, permissions). JWT issued by Identity, consistent format regardless of auth method.

Services validate tokens locally — no runtime call to Identity per request.

### The Identity Problem

### Capsule: NoUniversalTerm

**Invariant**
There is no agreed term for "a human being" across PushPay services.

**Example**
Same human: Identity (auth), CommunityMember (data), Person (legacy), User (mobile), Donor (giving).
//BOUNDARY: One human = ONE Identity, MANY CommunityMembers (multi-church).

**Depth**
- Login, JWT, passwords → "Identity"
- Name, address, household → "CommunityMember"
- Payment history → "Donor"

### Identity Merging

**IRREVERSIBLE.** Once two identities merge, original identity keys are gone. Event history preserved but cannot "unmerge" without manual data surgery.

---

## Constraints

- **Event-sourced**: All state changes are events. No rollback.
- **Permission package lag**: Identity deploys new permissions; services consume via NuGet package asynchronously.
- **Multi-region required**: DR replication must be active.
- **JWT is the only auth mechanism**: No service-to-service calls without valid token.

---

## What It Does NOT Do

- Store person/member data (Community Service)
- Store church organizational structure (Organization Service)
- Process payments (Giving domain)
- Verify passwords/MFA directly (delegated to Auth0/Okta)

---

## Deeper

- [Domain Model](domain-model.md) — Identity entity, account types
- [Architecture](architecture.md) — Event sourcing, CQRS, storage
- [Authentication System](authentication-system.md) — OAuth, tokens, MFA
- [Identity Merging](identity-merging.md) — Merge process, constraints

---

## Why This Works as Gestalt

**Essential essence in first paragraph**: JWT authority, centralized auth, foundation layer.

**Key concepts unlock understanding**:
- Authentication pattern (IdP → Identity → JWT → Services)
- The Identity Problem (vocabulary confusion)
- Irreversibility (the big gotcha)

**Constraints shape decisions**: Event-sourced, permission lag, multi-region.

**What it does NOT do**: Prevents scope confusion.

**Pointers to depth**: Links to detail, doesn't duplicate it.

**After reading**: You understand what Identity is, how it fits, what to be careful about. You could reason about where auth changes belong, predict blast radius, explain the system to someone else.
