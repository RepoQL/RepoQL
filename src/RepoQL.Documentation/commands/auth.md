---
description: "::login (PKCE or device-code), ::logout (clear tokens), ::whoami (show identity and expiry) — authentication commands"
tags: ["command", "login", "logout", "whoami", "auth", "oauth", "pkce", "device-code", "session"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# Auth Commands

Authenticate with cloud-backed features (inference, remote embeddings).

**Login:** `::login` or `::login[device-code]`
**Logout:** `::logout`
**Identity:** `::whoami`

---

## ::login

### Capsule: BrowserFlow

**Invariant**
`repoql login` uses OAuth2 authorization code + PKCE with a loopback callback on an OS-assigned localhost port.

**Example**
```bash
repoql login
→ Waiting for authentication...
→ Logged in as person@example.com
```

**Depth**
- Opens the default browser best-effort
- Falls back to printing the URL if the browser cannot be opened
- Stores the refresh token in the OS credential store when available
- Stores the access token in `~/.repoql/auth.json`

---

### Capsule: DeviceFlow

**Invariant**
`repoql login --device-code` works in SSH, WSL, and container sessions without a local callback listener.

**Example**
```bash
repoql login --device-code
→ To authenticate, visit: https://...
→ Enter code: ABCD-EFGH
→ Waiting for authentication...
→ Logged in as person@example.com
```

**Depth**
- Polls WorkOS at the interval returned by the device authorization response
- Prints a code-expired message when the device code times out
- In `::command` mode, use `::login[device-code]`

---

### Capsule: Errors

**Invariant**
Login failures map to recovery steps instead of raw protocol errors.

**Example**
```bash
repoql login
→ Authentication timed out. Run: repoql login to try again

repoql login --device-code
→ Code expired. Run: repoql login --device-code to try again
```

---

## ::logout

### Capsule: BasicUsage

**Invariant**
`repoql logout` clears the stored OAuth session without mutating any configured API key.

**Example**
```bash
repoql logout
→ Logged out
```

**Depth**
- Deletes `~/.repoql/auth.json`
- Clears the refresh token from the OS credential store or encrypted fallback file
- Returns `Not logged in` when no stored session exists

---

## ::whoami

### Capsule: SessionIdentity

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

### Capsule: ApiKeyFallback

**Invariant**
When only `cloud.api_key` is configured, `whoami` reports API key authentication instead of a session.

**Example**
```bash
repoql whoami
→ Authenticated via API key (hash prefix: 2BB80D53)
```

---

### Capsule: NoSession

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
repoql login --help
repoql logout --help
repoql whoami --help
```
