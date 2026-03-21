---
description: "ask(json_data, question, max_tokens) → LLM-synthesized answer text"
tags: ["ask", "llm", "json", "synthesis"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# ask

Ask an LLM to synthesize an answer from query-shaped JSON rows.

## Capsule: Ask

**Invariant**
`ask(json_data, question, max_tokens)` answers a question against the JSON rows you provide.

**Example**
```sql
WITH results AS (
    SELECT uri, headline, structure
    FROM search('authentication', k := 10)
)
SELECT ask(
    (SELECT json_group_array(json_object('uri', uri, 'headline', headline)) FROM results),
    'How is authentication implemented?',
    300
);
```

**Returns**

VARCHAR — synthesized answer text from the LLM.

**Depth**
- `json_data` should be a JSON array of result rows, typically built with `json_group_array(...)`
- Requires `OPENROUTER_API_KEY`; returns a helpful message when LLM support is not configured
- `max_tokens` is an approximate response budget, not a strict guarantee
