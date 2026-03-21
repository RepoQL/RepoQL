---
description: "git_file_history(uri) → hash, author_name, author_date, message, change_type"
tags: ["git_file_history", "git", "history", "file"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_file_history

Return indexed commit history for a single file.

## Capsule: GitFileHistory

**Invariant**
`git_file_history(uri)` reads indexed history for a file, including rename tracking.

**Example**
```sql
SELECT hash, author_name, message
FROM git_file_history('file:///src/Core.cs')
LIMIT 10;
```
//BOUNDARY: Queries indexed history (last 12 months). Includes renames across primary and imported repositories.

**Depth**
- Returns commit metadata plus `old_uri`, `insertions`, and `deletions`
- Works against local `file:///` and imported repository URIs
