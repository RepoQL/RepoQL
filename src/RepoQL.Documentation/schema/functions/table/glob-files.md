---
description: "glob_files(pattern_spec, uris) → uri"
tags: ["glob_files", "glob", "registry", "filtering"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# glob_files

Expand a glob pattern or URI list into matching registry URIs.

## Capsule: GlobFiles

**Invariant**
`glob_files(pattern_spec, uris)` returns URIs from the registry rather than walking the filesystem directly.

**Example**
```sql
SELECT uri
FROM glob_files(uris := (SELECT list(uri) FROM search('auth', k := 5)));
```

**Depth**
- `pattern_spec` supports compound patterns with `;`, exclusions with `!`, and fragments like `#symbol=` and `#line=`
- If both arguments are supplied, `uris` takes precedence over pattern matching
