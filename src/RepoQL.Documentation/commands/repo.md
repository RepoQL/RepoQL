---
description: "Switch the active repository without restarting the MCP server. Validates path, resolves repo root, auto-launches host."
tags: ["command", "repo", "repository", "switch", "context"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::repo

Switch the active repository. Disconnects from the current host and connects (or launches) a host for the new directory.

---

## Capsule: BasicUsage

**Invariant**
`::repo[path]` switches to the repository at `path`. If the path is inside a repo, it resolves to the root.

**Example**
```
::repo[C:\Source\OtherProject]
→ Switched to repository: C:\Source\OtherProject

::repo[C:\Source\OtherProject\src\deep\nested]
→ Switched to repository: C:\Source\OtherProject  (walked up to .git root)

::repo[/home/user/project]
→ Switched to repository: /home/user/project
```
//BOUNDARY: Host auto-launch takes 1-3 seconds. The command waits for connection before returning.

**Depth**
- Path is resolved via `Path.GetFullPath()` — relative paths work
- If `.git` or `.repoql` markers exist at or above the path, resolves to that root
- If no markers exist, asks for confirmation before creating a new `.repoql` index

---

## Capsule: Confirmation

**Invariant**
Directories without `.git` or `.repoql` markers require a repeat call to confirm.

**Example**
```
::repo[C:\Users\stuee\Desktop]
→ No repository markers (.git/.repoql) found at C:\Users\stuee\Desktop.
  RepoQL will create a new index here. Call ::repo[C:\Users\stuee\Desktop] again to confirm.

::repo[C:\Users\stuee\Desktop]
→ Switched to repository: C:\Users\stuee\Desktop
```

**Depth**
- First call sets a pending confirmation for the path
- Second consecutive call on the same path proceeds
- Calling with a different path resets the confirmation
- Directories with `.git` or `.repoql` skip confirmation entirely

---

## Capsule: Errors

**Invariant**
Clear error messages guide recovery for every failure mode.

**Example**
```
::repo[]
→ Path is required. Usage: ::repo[C:\Source\MyRepo]

::repo[/nonexistent/path]
→ Directory not found: /nonexistent/path

::repo[C:\Source\ValidRepo]  (host fails to launch)
→ Failed to connect to repository at C:\Source\ValidRepo: <details>
```

---

## Help

```
::repo --help
→ ::repo — Switch to a different repository
  Usage: ::repo[path]
    path  Path to repository directory
```
