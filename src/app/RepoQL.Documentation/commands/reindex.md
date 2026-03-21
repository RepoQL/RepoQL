---
description: "Reindex files, optionally scoped to a URI glob pattern. Triggers full pipeline re-processing."
tags: ["command", "reindex", "index", "refresh", "scope"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::reindex

Reindex files through the full pipeline. Optionally scope to a URI glob pattern to reindex only matching files.

---

## Capsule: BasicUsage

**Invariant**
`::reindex` reindexes all files. `::reindex[pattern]` reindexes only files matching the glob.

**Example**
```
::reindex
→ Reindex complete: 1234/1234 items.

::reindex[file:///src/**/*.cs]
→ Reindex complete: 89/89 items (scope: file:///src/**/*.cs).

::reindex[file:///docs/**]
→ Reindex complete: 42/42 items (scope: file:///docs/**).
```
//BOUNDARY: Reindexing is synchronous — the command waits for completion. Large scopes take longer.

**Depth**
- Scope uses the same URI glob syntax as explore/read tools
- Omitting scope reindexes everything (equivalent to `file:///**`)
- Supports exclusion patterns: `file:///src/**;!**/tests/**`
- Does not clear existing data — uses upsert semantics

---

## Help

```
::reindex --help
→ ::reindex — Reindex files, optionally scoped to a URI pattern
  Usage: ::reindex[scope?]
    scope  URI glob pattern (e.g., file:///src/**/*.cs). Omit for all.
```
