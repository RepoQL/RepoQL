---
description: "SQL query crafting for RepoQL. Views, functions, composition patterns, and graph traversal."
tags: ["skill", "sql-expert", "query", "sql", "views", "functions", "duckdb"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
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

---

## Capsule: ViewsFirst

**Invariant**
Start with views, not base tables. Views are the designed interface.

**Example**
Wrong: `SELECT * FROM artifact WHERE ...` — raw, unwieldy
Right: `SELECT * FROM Files WHERE ...` — designed for use
//BOUNDARY: Base tables (artifact, node, edge, span, annotation) exist but views should cover 90% of needs.

**Depth**

| View | Purpose | Key columns |
|------|---------|-------------|
| **Files** | Document inventory | uri, lang, lines, headline, summary, structure, error_count |
| **Types** | Classes, interfaces, structs | name, type_kind, extends, implements, file_uri |
| **Functions** | Methods, constructors, callables | name, signature, declaring_type, is_async, return_type |
| **Annotations** | Errors, warnings, lint | severity, rule_id, message, resolved_target_uri |
| **FileSystems** | Mounted file systems | scheme, mount_id, file_count |

Full column reference for each: `help:///repoql/tools/query/views/`

---

## Capsule: CompositionPatterns

**Invariant**
Use LATERAL to expand each row. Use functions for semantic operations.

**Example**
```sql
-- Search and show context
SELECT s.uri, sn.text
FROM search('config', k := 5) s,
LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```
//BOUNDARY: LATERAL is like a for-each loop in SQL — powerful for expansion.

**Depth**
- `search(q, k)` returns URIs ranked by relevance (hybrid: BM25 + fuzzy + semantic)
- `snippet(uri, context)` returns lines around a fragment location
- `search_symbol(q, scope, kind_filter, k)` finds named symbols
- `related(seed_uri, k)` finds similar files/symbols
- Combine with LATERAL for per-row expansion

Full function reference: `help:///repoql/tools/query/sql-reference.md`

---

## Capsule: GraphTraversal

**Invariant**
The graph has two edge types: composition (HAS_PART tree) and references (CALLS, REFERS_TO, etc.). Know which you're traversing.

**Example**
```sql
-- What's inside this file? (composition)
SELECT child.kind, child.name
FROM edge e
JOIN node child ON e.destination_node_id = child.id
WHERE e.is_composition = true
AND e.source_node_id = (SELECT id FROM node WHERE uri = @target);

-- Who references this? (non-composition)
SELECT src.uri, e.type
FROM edge e
JOIN node src ON e.source_node_id = src.id
WHERE e.is_composition = false
AND e.destination_node_id = (SELECT id FROM node WHERE name = 'MyClass');
```
//BOUNDARY: is_composition=true is the tree. is_composition=false is the graph.

**Depth**
- 5 frozen tables: `artifact`, `node`, `edge`, `span`, `annotation`
- Nodes have `kind` (e.g., `document`, `csharp.type`, `markdown.heading`)
- Edges have `type` (e.g., `HAS_PART`, `CALLS`, `REFERS_TO`, `EXTENDS`, `IMPLEMENTS`)
- Full schema: `help:///repoql/tools/query/schema.md`
- Pattern reference: `help:///repoql/tools/query/patterns/graph-traversal.md`

---

## Key Functions

| Function | Returns | Use for |
|----------|---------|---------|
| `search(q, k)` | URIs ranked by relevance | Finding code by concept |
| `snippet(uri, context)` | Lines around a location | Showing code context |
| `search_symbol(q, scope, kind, k)` | Symbol URIs | Finding named entities |
| `related(uri, k)` | Similar URIs | Finding related code |
| `annotations_for(uri)` | Diagnostics | File-level lint/errors |
| `ask(data, question)` | LLM answer | Summarizing query results |
| `glob_match(path, pattern)` | boolean | Path filtering |

Full signatures and examples: `help:///repoql/tools/query/sql-reference.md`

---

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

**Search + context (LATERAL):**
```sql
SELECT s.uri, sn.text
FROM search('config', k := 5) s,
LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

**Git history:**
```sql
SELECT * FROM git_log(10);
```

---

## Comments Steer Summarization

When query results exceed the token budget and the SQL contains comments, comments become a question for the LLM summarizer:

```sql
-- What are the most complex files and why?
SELECT uri, lines, error_count, headline
FROM Files
ORDER BY lines DESC
LIMIT 50;
```

The comment becomes the synthesis prompt. Use this when you want a narrative answer, not raw rows.

---

## Cross-References

- **View schemas**: `help:///repoql/tools/query/views/`
- **Function reference**: `help:///repoql/tools/query/sql-reference.md`
- **Graph schema**: `help:///repoql/tools/query/schema.md`
- **Query patterns**: `help:///repoql/tools/query/patterns/`
- **Data analysis (CSV, JSON, Excel, Parquet)**: `help:///repoql/tools/query/data-analysis.md`
- **MCP server queries**: `help:///repoql/tools/query/functions/mcp.md`

---

*Start with views. Compose with LATERAL. Traverse with edges.*
