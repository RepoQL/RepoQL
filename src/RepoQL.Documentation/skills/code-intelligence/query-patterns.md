---
description: "SQL patterns for structural code questions: counting, filtering, graph traversal, and cross-file analysis."
tags: ["skill", "code-intelligence", "query", "sql", "patterns", "graph"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Query Patterns for Code Intelligence

When explore and read answer "where" and "what," query answers "how many," "which ones," and "what patterns." For the full SQL function reference, see `help:///repoql/tools/query/sql-reference.md`.

---

## Inventory Queries

### Language distribution
```sql
SELECT lang, COUNT(*) as files, SUM(lines) as total_lines
FROM Files
GROUP BY lang
ORDER BY total_lines DESC;
```

### Largest files
```sql
SELECT uri, lines, headline
FROM Files
ORDER BY lines DESC
LIMIT 20;
```

### File health
```sql
SELECT uri, error_count, warning_count, headline
FROM Files
WHERE error_count > 0 OR warning_count > 0
ORDER BY error_count DESC;
```

---

## Type System Queries

### Find implementations of an interface
```sql
SELECT name, file_uri, type_kind
FROM Types
WHERE implements LIKE '%IFormatLoader%';
```

### Class hierarchy
```sql
SELECT name, extends, implements, type_kind
FROM Types
WHERE extends IS NOT NULL
ORDER BY extends, name;
```

### All async methods
```sql
SELECT name, declaring_type, signature, file_uri
FROM Functions
WHERE is_async = true;
```

---

## Search + Context

### Semantic search with snippets
```sql
SELECT s.uri, sn.text
FROM search('error handling', k := 5) s,
LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

### Find symbols by name
```sql
SELECT uri, name, kind
FROM search_symbol('Validate', k := 10);
```

---

## Graph Traversal

### Children of a node (composition tree)
```sql
SELECT child.kind, child.name, child.uri
FROM edge e
JOIN node child ON e.destination_node_id = child.id
WHERE e.is_composition = true
AND e.source_node_id = (SELECT id FROM node WHERE uri = 'file:///src/Foo.cs');
```

### Cross-references (who calls/uses what)
```sql
SELECT src.uri AS caller, dst.uri AS callee, e.type
FROM edge e
JOIN node src ON e.source_node_id = src.id
JOIN node dst ON e.destination_node_id = dst.id
WHERE e.is_composition = false
AND dst.name = 'ValidateToken';
```

For comprehensive graph traversal patterns, see `help:///repoql/tools/query/patterns/graph-traversal.md`.

---

## Annotations and Diagnostics

### All errors with context
```sql
SELECT a.resolved_target_uri, a.severity, a.rule_id, a.message
FROM Annotations a
WHERE a.severity = 'error'
ORDER BY a.resolved_target_uri;
```

### Errors per file
```sql
SELECT uri, error_count, warning_count
FROM Files
WHERE error_count > 0
ORDER BY error_count DESC;
```

---

## Cross-References

- **View schemas (Files, Types, Functions, Annotations)**: `help:///repoql/tools/query/views/`
- **All SQL functions**: `help:///repoql/tools/query/sql-reference.md`
- **Graph schema (5 tables)**: `help:///repoql/tools/query/schema.md`
- **Advanced patterns**: `help:///repoql/tools/query/patterns/`

---

*Query for computation. Explore for discovery.*
