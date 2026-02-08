---
name: sql-expert
description: SQL query crafting for RepoQL. Use when user needs aggregations, counting, cross-file analysis, graph traversal, or complex filtering. Triggers on "how many", "count the", "what calls", "find all X that Y", pattern matching queries.
---

# SQL Expert

Craft SQL queries for RepoQL's DuckDB database. The repository is indexed into tables and views you can query.

## When to Use Query

| Need | Tool |
|------|------|
| "What exists? Where is X?" | explore |
| "Show me this file/symbol" | read |
| "How many? Which ones? What pattern?" | **query** |

Query is for computation: aggregating, filtering, joining, pattern extraction, graph traversal.

## Capsule: ViewsFirst

**Invariant**
Start with views, not base tables. Views are the designed interface.

**Example**
Wrong: `SELECT * FROM artifact WHERE ...` - raw, unwieldy
Right: `SELECT * FROM Files WHERE ...` - designed for use
//BOUNDARY: Base tables (artifact, node, edge, span, annotation) exist but views should cover 90% of needs.

**Depth**
- **Files**: documents with diagnostics (uri, lang, lines, error_count, headline, summary, structure)
- **Functions**: methods/constructors (name, signature, declaring_type, is_async, return_type)
- **Types**: classes/interfaces/structs (name, type_kind, extends, implements)
- **Annotations**: errors/warnings/lint (severity, rule_id, message)

## Capsule: CompositionPatterns

**Invariant**
Use LATERAL to expand each row. Use functions for semantic operations.

**Example**
```sql
-- Search and show context
SELECT s.uri, sn.text
FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```
//BOUNDARY: LATERAL is like a for-each loop in SQL - powerful for expansion.

**Depth**
- `search(q, k)` returns URIs ranked by relevance
- `snippet(uri, context)` returns lines around a location
- `search_symbol(q, scope, kind_filter, k)` finds named symbols
- Combine with LATERAL for each-row expansion

## Quick Reference

**Count by language:**
```sql
SELECT lang, COUNT(*) FROM Files GROUP BY lang;
```

**Find errors:**
```sql
SELECT uri, error_count FROM Files WHERE error_count > 0;
```

**Search semantically:**
```sql
SELECT uri, score FROM search('authentication', k := 10);
```

**Find implementations:**
```sql
SELECT name, file_uri FROM Types WHERE extends = 'BaseService';
```

**Graph traversal (composition tree):**
```sql
SELECT child.kind, child.name FROM edge e
JOIN node child ON e.destination_node_id = child.id
WHERE e.type = 'HAS_PART' AND e.source_node_id = (SELECT id FROM node WHERE uri = @target);
```

## References

See `references/views.md` for complete view schemas.
See `references/functions.md` for all available functions.

## Learning from Docs

```sql
SELECT uri, headline FROM Files WHERE uri LIKE 'help://%';
```
