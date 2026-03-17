---
description: "snippet(uri, context_lines) → line_number, text, is_focus, document_uri"
tags: ["snippet", "content", "lines", "fragments"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# snippet

Extract lines from a document with context around a focal point.

## Capsule: Snippet

**Invariant**
`snippet(uri, context_lines)` resolves URI fragments such as `#line=` and `#symbol=` into focused line output.

**Example**
```sql
SELECT line_number, text
FROM snippet('file:///src/api.cs#line=42', 3);
```

**Depth**
- Supports `#line=N`, `#line=N,M`, `#symbol=Name`, and `#char=N,M` fragments
- `is_focus` marks the focal range inside the returned context window
