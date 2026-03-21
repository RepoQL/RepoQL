---
description: "snippet(uri, context_lines) → line_number, text, is_focus, focus_start_column, focus_end_column, language, document_uri, resolved_uri"
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

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `line_number` | INTEGER | Line number (1-based) |
| `text` | VARCHAR | Line content |
| `is_focus` | BOOLEAN | Whether this line is in the focal range |
| `focus_start_column` | INTEGER | Start column of the focus within the line |
| `focus_end_column` | INTEGER | End column of the focus within the line |
| `language` | VARCHAR | Language identifier for syntax highlighting |
| `document_uri` | VARCHAR | Container document URI (without fragment) |
| `resolved_uri` | VARCHAR | Full resolved URI including fragment |

**Depth**
- Supports `#line=N`, `#line=N,M`, `#symbol=Name`, and `#char=N,M` fragments
- `is_focus` marks the focal range inside the returned context window
