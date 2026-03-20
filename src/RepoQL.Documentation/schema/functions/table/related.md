---
description: "related(seed_uri, k, mode, uri_glob) → uri, kind, symbol, lang, headline, structure, snippet, line_start, line_end, score, confidence"
tags: ["related", "semantic", "similarity", "search"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# related

Find documents similar to a seed URI.

## Capsule: Related

**Invariant**
`related(seed_uri, k, mode, uri_glob)` is a "more like this" query over indexed documents.

**Example**
```sql
SELECT uri, score
FROM related('file:///src/Auth.cs', k := 10);
```
//BOUNDARY: "More like this" query. Uses seed's embedding for similarity.

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `uri` | VARCHAR | Document URI |
| `kind` | VARCHAR | Node kind (e.g. `document`, `symbol`) |
| `symbol` | VARCHAR | Symbol name (NULL for documents) |
| `lang` | VARCHAR | Language identifier |
| `headline` | VARCHAR | One-line x-ray summary |
| `structure` | VARCHAR | Signatures/outline |
| `snippet` | VARCHAR | Content preview |
| `line_start` | INTEGER | Start line (1-based) |
| `line_end` | INTEGER | End line (1-based) |
| `score` | DOUBLE | Final combined similarity score |
| `confidence` | DECIMAL(3,2) | Match confidence (0.00–1.00) |

**Depth**
- Excludes the seed document from the results
- Falls back to lexical signals when semantic signals are unavailable
