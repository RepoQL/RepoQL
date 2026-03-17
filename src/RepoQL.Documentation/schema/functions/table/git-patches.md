---
description: "git_patches(scope, include_unstaged) → uri, patch, insertions, deletions"
tags: ["git_patches", "git", "patch", "diff"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_patches

Return unified diff patches for working-copy changes.

## Capsule: GitPatches

**Invariant**
`git_patches(scope, include_unstaged)` returns live patch text for staged changes and, optionally, unstaged changes.

**Example**
```sql
SELECT uri, patch
FROM git_patches('src/Indexing/**', true);
```
//BOUNDARY: Live query against working copy. Returns actual unified diff content, not just file-level summaries like `git_diff()`.

**Depth**
- A file can appear twice when both staged and unstaged changes exist; use `COUNT(DISTINCT uri)` for summaries
- `diff_target` tells you whether each row is from the staged or unstaged diff
