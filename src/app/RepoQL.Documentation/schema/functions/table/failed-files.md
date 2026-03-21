---
description: "failed_files(pattern) → uri, status, error"
tags: ["failed_files", "operations", "indexing", "failures"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# failed_files

Return failed, skipped, or embedding-failed files with their error state.

## Capsule: FailedFiles

**Invariant**
`failed_files(pattern)` surfaces files that did not complete cleanly through indexing or embedding.

**Example**
```sql
SELECT uri, status, error
FROM failed_files('src/**');
```
//BOUNDARY: Pattern is optional (NULL = all files). Includes indexing failures, skipped files, and embedding failures.

**Depth**
- `status` distinguishes `Failed`, `Skipped`, and `Indexed` (indexing succeeded but embedding failed)
- Use a glob pattern to scope triage to one subtree or imported repository
