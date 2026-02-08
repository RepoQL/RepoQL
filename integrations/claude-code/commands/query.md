---
description: SQL interface - compute, aggregate, and analyze the codebase graph
allowed-tools: mcp__repoql__query
---

# RepoQL Query

DuckDB SQL for computation on the indexed repository. Use when you need to aggregate, filter, join, or extract patterns.

## Your Query

$ARGUMENTS

## When to Use Query

| Need | Tool |
|------|------|
| "What exists? Where is X?" | explore |
| "Show me this file/symbol" | read |
| "How many? Which ones? What pattern?" | **query** |

## Primary Views

**Files** - documents with diagnostics
```sql
SELECT uri, error_count FROM Files WHERE lang = 'code.csharp' AND error_count > 0;
SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang;
```

**Functions** - methods/constructors across languages
```sql
SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';
SELECT file_uri, name FROM Functions WHERE is_async AND return_type LIKE '%Task%';
```

**Types** - classes/interfaces/structs
```sql
SELECT name, file_uri FROM Types WHERE extends = 'BaseService';
SELECT name FROM Types WHERE type_kind = 'interface';
```

**Annotations** - errors/warnings/lint
```sql
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';
SELECT rule_id, COUNT(*) FROM Annotations GROUP BY rule_id ORDER BY 2 DESC;
```

## Key Functions

**search(q, k)** - semantic + lexical search
```sql
SELECT uri, score FROM search('authentication', k := 10);
```

**search_symbol(q, scope, kind_filter, k)** - find functions, classes by name
```sql
SELECT symbol, uri FROM search_symbol('ValidateToken');
```

**snippet(uri, context)** - code preview around location
```sql
SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);
```

**LATERAL** - expand each row
```sql
SELECT s.uri, sn.text
FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

## Learn More

Docs are queryable:
```sql
SELECT uri, headline FROM Files WHERE uri LIKE 'help://%';
```

Or use explore:
```
explore(intent=Locate, uriGlob="help://**", keywords="query functions", tokenBudget=1500)
```
