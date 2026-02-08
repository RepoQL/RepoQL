# Query Patterns

SQL patterns for codebase analysis.

## When to Use Query

| Need | Tool |
|------|------|
| "What exists? Where is X?" | explore |
| "Show me this file/symbol" | read |
| "How many? Which ones? What pattern?" | **query** |

Use query for: aggregating, complex filtering, joining results, regex extraction, graph traversal.

## Primary Views

### Files - Documents with Diagnostics

```sql
-- Files with errors
SELECT uri, error_count FROM Files WHERE lang = 'code.csharp' AND error_count > 0;

-- Language distribution
SELECT lang, COUNT(*) as files, SUM(lines) as total_lines FROM Files GROUP BY lang;

-- Largest files
SELECT uri, lines FROM Files ORDER BY lines DESC LIMIT 10;

-- Files modified recently (by headline pattern)
SELECT uri, headline FROM Files WHERE headline LIKE '%TODO%';
```

### Functions - Methods/Constructors

```sql
-- Methods in a class
SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';

-- Async methods returning Task
SELECT file_uri, name FROM Functions WHERE is_async AND return_type LIKE '%Task%';

-- Most complex functions (by signature length as proxy)
SELECT name, file_uri, LENGTH(signature) as complexity
FROM Functions ORDER BY complexity DESC LIMIT 20;

-- Find test methods
SELECT name, file_uri FROM Functions WHERE name LIKE 'Test%' OR name LIKE '%_Test';
```

### Types - Classes/Interfaces/Structs

```sql
-- Find implementations
SELECT name, file_uri FROM Types WHERE extends = 'BaseService';

-- All interfaces
SELECT name, file_uri FROM Types WHERE type_kind = 'interface';

-- Types implementing an interface
SELECT name, file_uri FROM Types WHERE implements LIKE '%IDisposable%';
```

### Annotations - Errors/Warnings/Lint

```sql
-- All errors
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';

-- Most common issues
SELECT rule_id, COUNT(*) as count FROM Annotations GROUP BY rule_id ORDER BY count DESC;

-- Errors by file
SELECT resolved_target_uri, COUNT(*) as errors
FROM Annotations WHERE severity = 'error'
GROUP BY resolved_target_uri ORDER BY errors DESC;
```

## Key Functions

### search(q, k) - Semantic + Lexical

```sql
SELECT uri, score FROM search('authentication', k := 10);
SELECT uri, score FROM search('error handling retry logic', k := 20);
```

### search_symbol(q, scope, kind_filter, k)

```sql
-- Find function by name
SELECT symbol, uri FROM search_symbol('ValidateToken');

-- Find types only
SELECT symbol, uri FROM search_symbol('Service', kind_filter := 'type');

-- Scoped search
SELECT symbol, uri FROM search_symbol('Handler', scope := 'src/**/*.cs', k := 20);
```

### snippet(uri, context) - Code Preview

```sql
SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);
```

### Composition with LATERAL

```sql
-- Search and show context
SELECT s.uri, sn.text
FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

## Advanced Patterns

### Graph Traversal

```sql
-- What symbols are inside this file?
SELECT child.kind, child.name FROM edge e
JOIN node child ON e.destination_node_id = child.id
WHERE e.type = 'HAS_PART'
AND e.source_node_id = (SELECT id FROM node WHERE uri = 'file:///src/Auth.cs');
```

### Regex Extraction

```sql
-- Find TODOs across codebase
SELECT uri, regexp_extract_all(text_content, 'TODO:\s*(.+)', 1) AS todos
FROM Files f JOIN artifact a ON f.artifact_id = a.id
WHERE text_content LIKE '%TODO:%';
```

### Cross-File Analysis

```sql
-- Find files that import a specific module
SELECT uri FROM Files
WHERE structure LIKE '%import%AuthService%';
```

## Learning More

Docs are queryable:

```sql
SELECT uri, headline FROM Files WHERE uri LIKE 'help://%';
SELECT uri FROM Files WHERE uri LIKE 'help:///repoql/tools/query/%';
```

Or use explore:
```
explore(intent=Locate, uriGlob="help://**", keywords="xlsx excel functions", tokenBudget=1500)
```
