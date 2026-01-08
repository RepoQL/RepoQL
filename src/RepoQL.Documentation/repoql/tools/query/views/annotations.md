---
description: "Annotations(id, kind, severity, source, message, data, scope_document_id, target_node_id, target_span_id, target_edge_id, target_uri, resolved_target_uri, severity_rank)"
tags: ["Annotations", "Diagnostics", "Lint", "Errors", "Warnings", "CodeAnalysis"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Annotations View

Diagnostics, lint results, and metadata annotations with resolved target URIs.

## Quick Reference

```sql
-- All errors
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';

-- Warnings by source
SELECT source, COUNT(*) FROM Annotations WHERE severity = 'warning' GROUP BY source;

-- Annotations for a file
SELECT severity, message FROM Annotations
WHERE resolved_target_uri LIKE 'file:///src/MyFile.cs%';
```

---

## Capsule: AnnotationsBasic

**Invariant**
`Annotations` extends the base `annotation` table with `resolved_target_uri` computed from available target references.

**Example**
```sql
SELECT severity, source, message FROM Annotations WHERE kind = 'lint';
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';
SELECT * FROM Annotations WHERE source = 'eslint';
```
//BOUNDARY: `resolved_target_uri` is computed; prefer it over raw target columns for consistent URIs.

**Depth**
- Resolves target from: `target_uri` → `target_span_id` → `target_node_id` → `target_edge_id` → `scope_document_id`
- First non-null wins in resolution order
- `data` is JSON with annotation-specific details
- SeeAlso: `annotations_for()` macro for simpler queries, `Files.error_count` for aggregates

---

## Capsule: AnnotationsSeverity

**Invariant**
`severity` is one of: `error`, `warning`, `info`, `hint`. `severity_rank` enables sorting.

**Example**
```sql
-- Errors first, then warnings
SELECT resolved_target_uri, severity, message
FROM Annotations ORDER BY severity_rank DESC;

-- Count by severity
SELECT severity, COUNT(*) FROM Annotations GROUP BY severity;

-- Only actionable items
SELECT * FROM Annotations WHERE severity IN ('error', 'warning');
```
//BOUNDARY: `severity_rank`: error=4, warning=3, info=2, hint=1, other=0.

**Depth**
- `severity_rank` computed by `_severity_rank()` macro
- Use for ORDER BY to get most severe first
- NULL/unknown severity gets rank 0
- Filter by severity for focused views

---

## Capsule: AnnotationsSource

**Invariant**
`source` identifies the tool/analyzer that created the annotation.

**Example**
```sql
-- All sources
SELECT DISTINCT source FROM Annotations;

-- ESLint issues
SELECT resolved_target_uri, message FROM Annotations WHERE source = 'eslint';

-- C# analyzer issues
SELECT message, data FROM Annotations WHERE source LIKE 'CA%' OR source LIKE 'CS%';

-- Group by tool
SELECT source, severity, COUNT(*)
FROM Annotations GROUP BY source, severity ORDER BY source;
```

**Depth**
- Common sources: `eslint`, `typescript`, `csharp`, rule IDs like `CA1000`, `CS8600`
- `source` may contain rule ID or analyzer name
- Use for filtering to specific tools

---

## Capsule: AnnotationsTargets

**Invariant**
Annotations can target documents, nodes, spans, edges, or explicit URIs.

**Example**
```sql
-- Annotations on specific nodes
SELECT a.message, n.headline
FROM Annotations a
JOIN node n ON n.id = a.target_node_id
WHERE a.target_node_id IS NOT NULL;

-- Document-level annotations
SELECT scope_document_id, COUNT(*) FROM Annotations GROUP BY scope_document_id;

-- Using resolved URI (preferred)
SELECT resolved_target_uri, message FROM Annotations
WHERE resolved_target_uri LIKE 'file:///src/%';
```

**Depth**
- `scope_document_id`: Always set, identifies containing document
- `target_node_id`: Specific node (function, type, etc.)
- `target_span_id`: Specific line range
- `target_edge_id`: Relationship annotation
- `target_uri`: Explicit URI (highest priority)
- `resolved_target_uri`: Computed best URI for display

---

## Capsule: AnnotationsData

**Invariant**
`data` is a JSON object with annotation-specific metadata.

**Example**
```sql
-- Annotations with fix suggestions
SELECT message, data->>'fix' AS fix FROM Annotations WHERE data->>'fix' IS NOT NULL;

-- Rule details
SELECT message, data->>'rule_url' AS docs FROM Annotations WHERE data->>'rule_url' IS NOT NULL;

-- Custom metadata
SELECT message, data FROM Annotations WHERE json_keys(data) != '[]';
```

**Depth**
- Schema varies by source/kind
- May contain: `fix`, `rule_id`, `rule_url`, `category`, `related_info`
- Use `json_extract` or `->>`/`->` operators
- NULL for simple annotations without extra data

---

## Common Patterns

| Goal | Query |
|------|-------|
| All annotations | `SELECT * FROM Annotations` |
| Errors only | `WHERE severity = 'error'` |
| By source | `WHERE source = 'eslint'` |
| In file | `WHERE resolved_target_uri LIKE 'file:///path%'` |
| Sorted by severity | `ORDER BY severity_rank DESC` |
| Count by severity | `GROUP BY severity` |
| With fixes | `WHERE data->>'fix' IS NOT NULL` |
| Document aggregates | `GROUP BY scope_document_id` |

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `id` | uuid | Annotation ID |
| `kind` | string | Annotation kind (`lint`, `metric`, etc.) |
| `severity` | string | `error`, `warning`, `info`, `hint` |
| `source` | string | Tool/analyzer name or rule ID |
| `message` | string | Human-readable message |
| `data` | json | Additional metadata |
| `scope_document_id` | uuid | Containing document's node ID |
| `target_node_id` | uuid | Target node (optional) |
| `target_span_id` | uuid | Target span (optional) |
| `target_edge_id` | uuid | Target edge (optional) |
| `target_uri` | string | Explicit target URI (optional) |
| `resolved_target_uri` | string | Computed best URI for target |
| `severity_rank` | integer | Numeric rank (error=4, warning=3, info=2, hint=1) |
