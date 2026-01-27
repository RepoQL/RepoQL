---
name: repoql-sql
description: SQL reference for RepoQL query tool - views, functions, and patterns.
---

# RepoQL SQL Reference

Use `repoql_query` for DuckDB SQL queries on indexed repository data.

## Core Views

### Files
Document inventory with metadata.
```sql
SELECT uri, lang, lines, error_count, headline, summary
FROM Files
WHERE lang = 'csharp'
ORDER BY lines DESC
LIMIT 10
```

| Column | Description |
|--------|-------------|
| uri | File URI (file:///path) |
| lang | Detected language |
| lines | Line count |
| error_count | Parse errors |
| headline | One-line summary |
| summary | Multi-line summary |

### Functions
Callable units (methods, functions).
```sql
SELECT name, qualified_name, signature, declaring_type, is_async
FROM Functions
WHERE declaring_type = 'AuthService'
```

| Column | Description |
|--------|-------------|
| name | Function name |
| qualified_name | Full path (Namespace.Class.Method) |
| signature | Parameter signature |
| declaring_type | Containing type |
| is_async | Async marker |

### Types
Type definitions (classes, interfaces, enums).
```sql
SELECT name, qualified_name, type_kind, extends, implements
FROM Types
WHERE type_kind = 'interface'
```

| Column | Description |
|--------|-------------|
| name | Type name |
| qualified_name | Full path |
| type_kind | class/interface/enum/struct |
| extends | Base type |
| implements | Implemented interfaces (array) |

### Annotations
Diagnostics, lint results, facts.
```sql
SELECT resolved_target_uri, severity, rule_id, message
FROM Annotations
WHERE severity = 'error'
```

## Key Functions

### search(query, k)
Semantic + lexical hybrid search.
```sql
SELECT * FROM search('authentication flow', k := 10)
SELECT * FROM search('config validation', k := 5)
```

### search_symbol(name)
Find symbols by name.
```sql
SELECT * FROM search_symbol('ValidateToken')
SELECT * FROM search_symbol('IAuth%')  -- Wildcards work
```

### snippet(uri, context)
Code preview around location.
```sql
SELECT snippet(uri, 3) FROM Files WHERE uri LIKE '%Auth%'
```

## Common Patterns

**Find files with errors:**
```sql
SELECT uri, error_count FROM Files WHERE error_count > 0
```

**Count functions per file:**
```sql
SELECT f.uri, COUNT(*) as fn_count
FROM Functions fn
JOIN Files f ON fn.file_uri = f.uri
GROUP BY f.uri
ORDER BY fn_count DESC
```

**Find async methods:**
```sql
SELECT qualified_name, signature
FROM Functions
WHERE is_async = true
```

**Search within scope:**
```sql
SELECT * FROM search('error handling', k := 10)
WHERE uri LIKE 'file:///src/services/%'
```
