---
description: "git_status(scope, include_untracked, include_ignored) → uri, category, index_status, work_tree_status"
tags: ["git_status", "git", "working-copy", "status"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_status

Return working-copy status equivalent to `git status --porcelain`.

## Capsule: GitStatus

**Invariant**
`git_status(scope, include_untracked, include_ignored)` queries the live working copy, not indexed history.

**Example**
```sql
SELECT uri, category
FROM git_status('src/**/*.cs');
```
//BOUNDARY: Live query against working copy. Not from indexed history.

**Depth**
- `category` normalizes the porcelain state into values like `staged`, `modified`, `staged+modified`, `untracked`, `conflict`, and `ignored`
- `scope` accepts compound globs such as `'**/*.cs;!**/tests/**'`
