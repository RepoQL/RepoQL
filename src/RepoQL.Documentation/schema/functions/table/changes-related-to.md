---
description: "changes_related_to(keywords, since, until, k) → commit, hash, date, author, message, files_changed, related_files, files, insertions, deletions"
tags: ["changes_related_to", "git", "semantic", "history"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# changes_related_to

Find commits that touched files semantically related to a concept.

## Capsule: ChangesRelatedTo

**Invariant**
`changes_related_to(keywords, since, until, k)` combines semantic file search with indexed git history.

**Example**
```sql
SELECT commit, message, files
FROM changes_related_to('UDF implementation', since := '30 days');
```
//BOUNDARY: Combines semantic search with git history. Finds conceptually related changes, not just keyword matches.

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `commit` | VARCHAR | Short commit hash |
| `hash` | VARCHAR | Full commit hash |
| `date` | DATE | Commit date |
| `author` | VARCHAR | Author name |
| `message` | VARCHAR | Commit message (first line) |
| `files_changed` | INTEGER | Total files changed in the commit |
| `related_files` | BIGINT | Number of changed files that matched the semantic search |
| `files` | VARCHAR | Semicolon-delimited list of related changed URIs |
| `insertions` | INTEGER | Lines added |
| `deletions` | INTEGER | Lines removed |

**Depth**
- `k` limits how many semantically related files are considered before history lookup
- The `files` column is a semicolon-delimited list of the related changed URIs, useful for follow-up reads
