---
description: "Show the current RepoQL authentication identity, method, and expiry."
tags: ["command", "whoami", "auth", "identity"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::whoami

Display the current RepoQL authentication identity.

---

## Capsule: SessionIdentity

**Invariant**
When RepoQL has a stored OAuth session, `whoami` shows the user identity from the stored JWT payloads.

**Example**
```bash
repoql whoami
→ Email: person@example.com
  User ID: user_123
  Auth method: session
  Token expiry: 2026-03-12T12:34:56.0000000+00:00
```

---

## Capsule: ApiKeyFallback

**Invariant**
When only `cloud.api_key` is configured, `whoami` reports API key authentication instead of a session.

**Example**
```bash
repoql whoami
→ Authenticated via API key (hash prefix: 2BB80D53)
```

---

## Capsule: NoSession

**Invariant**
Without a stored session or API key, RepoQL tells you how to recover.

**Example**
```bash
repoql whoami
→ Not logged in. Run: repoql login
```

---

## Help

```bash
repoql whoami --help
::whoami --help
```
