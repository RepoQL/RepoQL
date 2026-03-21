---
description: "embed_status() → embedding provider status text"
tags: ["embed_status", "embeddings", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# embed_status

Report the current embedding provider configuration as text.

## Capsule: EmbedStatus

**Invariant**
`embed_status()` returns the host's current embedding provider state.

**Example**
```sql
SELECT embed_status();
```

**Depth**
- Includes provider type, enabled status, model name, and embedding dimension
- Use it to distinguish configuration problems from ordinary semantic-search warmup
