---
description: "git_recent — commits from the last 7 days: hash, author_name, author_email, author_date, message, files_changed, insertions, deletions"
tags: ["git", "recent", "commits", "history"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# git_recent

Commits from the last 7 days with author, timestamp, message, and line-change counts.

## Quick Reference

```sql
-- Recent commits
SELECT hash, author_name, author_date, message FROM git_recent ORDER BY author_date DESC;

-- Who's been active this week?
SELECT author_name, COUNT(*) as commits, SUM(insertions + deletions) as churn
FROM git_recent GROUP BY author_name ORDER BY commits DESC;

-- Large commits
SELECT hash, message, files_changed, insertions, deletions
FROM git_recent ORDER BY insertions + deletions DESC LIMIT 5;
```

---

## Capsule: GitRecent

**Invariant**
`git_recent` surfaces commits authored within the last 7 days. Each row is one commit.

**Example**
```sql
-- Commits touching many files
SELECT hash, message, files_changed FROM git_recent WHERE files_changed > 5;

-- Net line change per commit
SELECT hash, message, insertions - deletions AS net_lines FROM git_recent;

-- Commits by a specific author
SELECT author_date, message FROM git_recent WHERE author_name = 'Alice';
```
//BOUNDARY: Window is 7 days from the current time. Commits older than 7 days do not appear — use `git_hotspots` for longer-horizon analysis.

**Depth**
- `hash`: Full 40-character SHA-1
- `message`: Full commit message including body; trim with `LEFT(message, 72)` for display
- `author_date`: Timezone-aware timestamp of the commit
- `insertions` / `deletions`: Line counts from the git diff stat; `files_changed` is the number of files touched
- SeeAlso: `git_hotspots` for all-time change frequency ranking

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `hash` | string | Full commit SHA-1 |
| `author_name` | string | Commit author display name |
| `author_email` | string | Commit author email |
| `author_date` | timestamp | When the commit was authored (timezone-aware) |
| `message` | string | Full commit message |
| `files_changed` | integer | Number of files touched |
| `insertions` | integer | Lines added |
| `deletions` | integer | Lines removed |
