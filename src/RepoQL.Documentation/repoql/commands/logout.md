---
description: "Clear the locally stored RepoQL session tokens."
tags: ["command", "logout", "auth", "session"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::logout

Remove the locally cached RepoQL access token and refresh token.

---

## Capsule: BasicUsage

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

## Help

```bash
repoql logout --help
::logout --help
```
