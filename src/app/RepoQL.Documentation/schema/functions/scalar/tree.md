---
description: "tree(uris_json, headlines_json, foldersOnly) → ASCII directory tree text"
tags: ["tree", "directory", "structure", "visualization"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# tree

Format URI and headline arrays as an ASCII directory tree.

## Capsule: Tree

**Invariant**
`tree(uris_json, headlines_json, foldersOnly)` renders aligned JSON arrays into a scheme-aware directory tree.

**Example**
```sql
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri LIKE 'file:///src/RepoQL.ConsoleApp/%';
```
//BOUNDARY: Inputs must be JSON arrays aligned by index. Empty array returns empty string.

**Depth**
- Groups URIs by scheme and sorts alphabetically with directories before files
- `foldersOnly := true` switches to aggregated folder counts by extension instead of full file listings
- Headlines are appended when any non-empty headline is present; pass `[]` to suppress them
