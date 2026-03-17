---
description: "annotations_for(uri, kinds, min_severity) → resolved_target_uri, severity, rule_id, message"
tags: ["annotations_for", "annotations", "diagnostics", "lint"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# annotations_for

Return diagnostics for a single indexed document.

## Capsule: AnnotationsFor

**Invariant**
`annotations_for(uri, kinds, min_severity)` filters annotations for one document URI.

**Example**
```sql
SELECT rule_id, message, resolved_target_uri
FROM annotations_for('file:///src/api.cs', NULL, 'error');
```

**Depth**
- `kinds` is a comma-separated filter such as `'lint,diagnostic'`; NULL means all kinds
- `min_severity` accepts `error`, `warning`, `info`, or `hint`
