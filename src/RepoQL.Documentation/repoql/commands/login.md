---
description: "Authenticate RepoQL with WorkOS using either a browser PKCE flow or a device-code flow."
tags: ["command", "login", "auth", "oauth", "pkce", "device-code"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::login

Authenticate the local RepoQL client for cloud-backed features such as inference and remote embeddings.

---

## Capsule: BrowserFlow

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

## Capsule: DeviceFlow

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

## Capsule: Errors

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

## Help

```bash
repoql login --help
::login --help
```
