---
description: "changes_related_to(keywords, since, until, k) → commit, date, author, message, related_files"
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

**Depth**
- `k` limits how many semantically related files are considered before history lookup
- The `files` column is a semicolon-delimited list of the related changed URIs, useful for follow-up reads
