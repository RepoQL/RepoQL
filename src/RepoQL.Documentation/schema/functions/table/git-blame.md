---
description: "git_blame(scope, start_line, end_line) → uri, line_number, author_name, commit_hash, message"
tags: ["git_blame", "git", "blame", "history"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_blame

Return line-by-line blame information for files or fragments.

## Capsule: GitBlame

**Invariant**
`git_blame(scope, start_line, end_line)` computes live blame data at query time.

**Example**
```sql
SELECT uri, line_number, author_name, message
FROM git_blame('src/**/*.cs', 1, 50);
```
//BOUNDARY: Live blame computed at query time. Can be slow for large files.

**Depth**
- `scope` accepts a file URI or glob pattern
- Line ranges are 1-based and are the main way to keep blame queries cheap on large files
