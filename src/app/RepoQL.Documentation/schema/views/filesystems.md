---
description: "Filesystems(source_uri, file_count, languages, total_tokens, embed_pct)"
tags: ["Filesystems", "Imports", "Mounts", "Sources"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Filesystems View

Summary of data sources with file counts, languages, tokens, and embedding progress.

```sql
SELECT source_uri, file_count, languages, embed_pct FROM Filesystems;
```

## Capsule: Filesystems

**Invariant**
`Filesystems` aggregates stats and embedding progress per data source.

**Example**
```sql
SELECT source_uri, file_count, total_tokens, embed_pct FROM Filesystems;
```
//BOUNDARY: Shows sources with indexed files; empty imports not listed until indexed.

**Depth**
- `languages`: Comma-separated list of media types in source
- `embed_pct`: Percentage of documents with embeddings
- SeeAlso: `Files`, `import` tool
