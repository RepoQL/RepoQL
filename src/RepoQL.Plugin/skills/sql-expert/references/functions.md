# RepoQL Functions Reference

Functions extend SQL with semantic and codebase-aware operations.

## Search Functions

### search(q, k)

Semantic + lexical document search. Returns documents ranked by relevance.

```sql
SELECT uri, score FROM search('authentication', k := 10);
SELECT uri, score FROM search('error handling retry logic', k := 20);
```

| Parameter | Type | Description |
|-----------|------|-------------|
| q | VARCHAR | Search query (concepts work best) |
| k | INTEGER | Number of results (default 10) |

Returns: uri, score

### search_symbol(q, scope, kind_filter, k)

Find functions, classes, methods by name.

```sql
SELECT symbol, uri FROM search_symbol('ValidateToken');
SELECT symbol, uri FROM search_symbol('Service', kind_filter := 'type', scope := 'src/**/*.cs');
```

| Parameter | Type | Description |
|-----------|------|-------------|
| q | VARCHAR | Symbol name pattern |
| scope | VARCHAR | URI glob to search within |
| kind_filter | VARCHAR | 'function', 'type', or null for all |
| k | INTEGER | Number of results |

Returns: symbol, uri, kind

### related(uri, k)

Find documents similar to a given document.

```sql
SELECT uri, score FROM related('file:///src/Auth.cs', k := 10);
```

## Content Functions

### snippet(uri, context)

Code preview around a location. Use with LATERAL for expansion.

```sql
SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);

-- Expand search results
SELECT s.uri, sn.text
FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

| Parameter | Type | Description |
|-----------|------|-------------|
| uri | VARCHAR | URI with location fragment |
| context | INTEGER | Lines of context around location |

Returns: line_number, text, is_focus

### glob_files(pattern)

Path pattern matching. Returns URIs matching glob.

```sql
SELECT uri FROM glob_files('src/**/*.cs');
SELECT uri FROM glob_files('src/**/*.cs;!src/**/tests/**');
```

### tree(uris_json, headlines_json, foldersOnly)

Format URIs as ASCII directory tree.

```sql
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri LIKE 'file:///src/%';
```

## LLM Functions

### ask(data, question)

LLM-powered question answering on query results.

```sql
SELECT ask(
    (SELECT json_group_array(json_object('uri', uri)) FROM search('auth', k := 5)),
    'How is authentication implemented?'
);
```

## Git Functions

### git_status()

Current git status.

```sql
SELECT * FROM git_status();
```

### git_diff()

Current uncommitted changes.

```sql
SELECT * FROM git_diff();
```

### git_blame(uri)

Line-by-line attribution.

```sql
SELECT * FROM git_blame('file:///src/Auth.cs');
```

### git_hotspots

View of frequently changed files.

```sql
SELECT * FROM git_hotspots LIMIT 10;
```

### changes_related_to(uri, depth)

Find files that commonly change together.

```sql
SELECT * FROM changes_related_to('file:///src/Auth.cs', 10);
```

## MCP Functions

### mcp_tools()

List available MCP tools from connected servers.

```sql
SELECT * FROM mcp_tools();
```

### mcp_tool_params()

List tool parameters with documentation.

```sql
SELECT * FROM mcp_tool_params();
```

### parse(text)

Convert CSV/TSV/YAML/JSON/JSONL to rows.

```sql
SELECT * FROM parse('id,name\n1,Alice\n2,Bob');
```

## Composition with LATERAL

LATERAL is like a for-each loop - apply a function to each row.

```sql
-- Search and expand with snippets
SELECT s.uri, sn.line_number, sn.text
FROM search('config', k := 5) s,
LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;

-- Multiple levels
SELECT f.uri, t.name, t.type_kind
FROM glob_files('src/**/*.cs') f,
LATERAL (SELECT * FROM Types WHERE file_uri = f.uri) t;
```

## Learning More

Documentation is queryable:

```sql
SELECT uri, headline FROM Files WHERE uri LIKE 'help:///repoql/tools/query/functions/%';
```
