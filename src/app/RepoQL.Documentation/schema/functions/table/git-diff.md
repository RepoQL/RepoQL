---
description: "git_diff(from_ref, to_ref, scope) → uri, change_type, old_uri, insertions, deletions, is_binary"
tags: ["git_diff", "git", "diff", "history"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_diff

Return file-level changes between two refs.

## Capsule: GitDiff

**Invariant**
`git_diff(from_ref, to_ref, scope)` computes a live ref-to-ref diff when the query runs.

**Example**
```sql
SELECT uri, change_type, insertions, deletions
FROM git_diff('HEAD~5', 'HEAD', 'src/**/*.cs');
```
//BOUNDARY: Live diff computed at query time. Use indexed history for historical queries.

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `uri` | VARCHAR | File URI |
| `change_type` | VARCHAR | Git change code: `A` (add), `M` (modify), `D` (delete), `R` (rename), `C` (copy) |
| `old_uri` | VARCHAR | Previous URI (for renames/copies) |
| `insertions` | INTEGER | Lines added |
| `deletions` | INTEGER | Lines removed |
| `is_binary` | BOOLEAN | Whether the file is binary |

**Depth**
- `change_type` uses standard git codes such as `A`, `M`, `D`, `R`, and `C`
- Use `git_patches()` instead when you need unified diff text rather than file summaries
