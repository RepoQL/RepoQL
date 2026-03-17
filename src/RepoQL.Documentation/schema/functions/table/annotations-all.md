---
description: "annotations_all(kinds, min_severity) → resolved_target_uri, severity, rule_id, message"
tags: ["annotations_all", "annotations", "diagnostics", "lint"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# annotations_all

Return filtered annotations across all indexed documents.

## Capsule: AnnotationsAll

**Invariant**
`annotations_all(kinds, min_severity)` filters the global annotation set without requiring a document URI.

**Example**
```sql
SELECT rule_id, COUNT(*)
FROM annotations_all(NULL, 'warning')
GROUP BY rule_id;
```

**Depth**
- Same filters as `annotations_for()` but without a URI scope
- Returns the same resolved annotation shape as the `Annotations` view, so it is ready for direct aggregation
