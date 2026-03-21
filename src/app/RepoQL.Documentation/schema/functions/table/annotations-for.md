---
description: "annotations_for(uri, kinds, min_severity) → kind, severity, source, rule_id, message, data, target_uri, resolved_target_uri, created_at"
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

**Returns**

| Column | Type | Description |
|--------|------|-------------|
| `kind` | VARCHAR | Annotation kind (e.g. `lint`, `diagnostic`, `hint`) |
| `severity` | VARCHAR | Severity level: `error`, `warning`, `info`, `hint` |
| `source` | VARCHAR | Origin of the annotation (e.g. analyzer name) |
| `rule_id` | VARCHAR | Rule identifier |
| `message` | VARCHAR | Human-readable diagnostic message |
| `data` | JSON | Structured metadata attached to the annotation |
| `target_uri` | VARCHAR | Raw target URI |
| `resolved_target_uri` | VARCHAR | Resolved target URI with line information |
| `created_at` | TIMESTAMP | When the annotation was created |

**Depth**
- `kinds` is a comma-separated filter such as `'lint,diagnostic'`; NULL means all kinds
- `min_severity` accepts `error`, `warning`, `info`, or `hint`
