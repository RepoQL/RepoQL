<WHY>
The index has already parsed everything. Query gives you DuckDB SQL over the entire graph — count, list, traverse relationships, join with git history, parse data files, call external MCP servers. One query surface for code, data, history, and external tools.
Any file containing structured data becomes a table (e.g. csv, json, parquet, excel)
RepoQL is wild magic — every operation returns a table, and tables compose with SQL. Joining other mcp server tools together with repository data. CTEs, LATERAL joins, recursive traversals — if you can express it in SQL, you can compute it. Your instincts are probably right — try them.
</WHY>

<WHEN_TO_USE>
Query is very powerful, but if explore or read satisfy your use case, use them instead - they will do a better job at what they are built for.
</WHEN_TO_USE>

<VIEWS>
Start here — these cover 90% of queries:

**Files** — `uri, source, path, dirname, name, extension, mime, byte_size, content_category, headline, summary, structure, token_count, mtime, node_id, artifact_id`
```sql
SELECT content_category, COUNT(*), SUM(token_count) FROM Files GROUP BY content_category;
```
`content_category` is one of: `code`, `document`, `structured-data`, `plaintext`, `image`, `video`, `audio`, `archive`, `binary`. For language, filter on `mime` or `extension`.

**Functions** — `uri, name, qualified_name, function_kind, declaring_type, visibility, signature, return_type, parameters, lang, is_static, is_async, headline, structure, start_line, end_line, node_id`
```sql
SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';
```

**Types** — `uri, name, qualified_name, type_kind, namespace, visibility, signature, lang, extends, implements, headline, structure, start_line, end_line, node_id`
```sql
SELECT name, uri FROM Types WHERE extends = 'BaseService';
```
Supported languages usually have more tailored views prefixed with their extension, e.g. `csharp_types`, `python_imports`. Use the explore tool on `help://**` to discover them.

**Annotations** — `id, kind, severity, source, rule_id, message, details, document_id, document_uri, target_node_id, target_uri, severity_rank, created_at` (plus raw `annotation.*` columns)
```sql
SELECT rule_id, COUNT(*) FROM Annotations GROUP BY rule_id ORDER BY 2 DESC;
SELECT severity, message FROM Annotations WHERE document_uri = 'file:///src/api.cs';
```
</VIEWS>

<FUNCTIONS>
**search(q, k, scope, boost_pattern, negative_pattern)** → uri, score — semantic + lexical
```sql
SELECT uri, score FROM search('authentication', k := 10);
SELECT uri, score FROM search('parser', scope := 'file:///src/%', boost_pattern := 'markdown|yaml', negative_pattern := '(?i)test');
```

**glob_files(pattern)** → uri — `SELECT uri FROM glob_files('src/**/*.cs;!**/tests/**');`
**related(uri, k)** → uri, score — find similar documents
**ask(context_json, question, max_tokens)** → text — LLM synthesis on query results
</FUNCTIONS>

<COMPOSITION>
Every operation returns a table. SQL joins and CTEs compose them.

**LATERAL** — expand each row with a correlated function:
```sql
SELECT s.uri, sn.text
FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

**parse()** — inline CSV/JSON/YAML/anything as ad-hoc lookup tables:
```sql
SELECT f.uri, o.team FROM Files f
JOIN parse('pattern,team\n**/Auth/**,Security\n**/Core/**,Platform') o
ON f.uri LIKE o.pattern;
```

**Recursive CTEs** — graph traversal through composition tree:
```sql
WITH RECURSIVE parts AS (
  SELECT destination_node_id as id, 1 as depth FROM edge
  WHERE source_node_id = (SELECT id FROM node WHERE uri = 'file:///src/Auth.cs')
  AND type = 'HAS_PART'
  UNION ALL
  SELECT e.destination_node_id, p.depth + 1 FROM edge e
  JOIN parts p ON e.source_node_id = p.id
  WHERE e.type = 'HAS_PART' AND p.depth < 5
)
SELECT n.kind, n.name, p.depth FROM parts p
JOIN node n ON p.id = n.id ORDER BY p.depth;
```

**Search + enrich** — join search results with metadata:
```sql
SELECT s.uri, f.content_category, f.token_count FROM search('error', k := 20) s JOIN Files f ON s.uri = f.uri;
```
</COMPOSITION>

<FUNCTIONALITY>
DuckDB's `EXPLAIN` and `SUMMARIZE` keywords are the fastest way to learn the shape of any view or query — reach for them when something looks unfamiliar.

- **Git**: `git_status()`, `git_diff()`, `git_blame()`, `git_hotspots`, `changes_related_to()`
- **MCP**: `mcp_tools()`, `mcp_tool_params()` — call external MCP servers from SQL, results as rows
- **Data**: `parse(text)` for CSV/JSON/YAML; `xlsx()`, `xlsx_sheets()`, `xlsx_union()` for Excel
- **Format views**: `markdown_headings`, `csharp_types`, etc. — `help:///repoql/tools/query/formats/*`
- **Regex**: `regexp_extract_all()` for pattern extraction across the codebase
- **DuckDB patterns**: `QUALIFY`, `PIVOT/UNPIVOT`, list comprehensions, window functions
- **Base tables** (prefer views): `artifact`, `node`, `edge`, `annotation`, `embeddings`

Full guidance: `help:///tools/query.md`
Schema: `help:///schema/core.md;help:///schema/views/*.md`
Functions/macros: `help:///schema/functions/**/*.md`
Format/language-specific schema: `help:///formats/*.md`
Useful SQL patterns: `help:///patterns/*.md`
Calling other MCP servers: `help:///mcp-bridge/sql.md`
</FUNCTIONALITY>

<QUICK_PATTERNS>
Orient in a new codebase — what categories of content exist and how much:
```sql
SELECT content_category, COUNT(*) AS files, SUM(token_count) AS tokens
FROM Files GROUP BY 1 ORDER BY 3 DESC;
```

Find the biggest functions in a subsystem:
```sql
SELECT name, end_line - start_line + 1 AS lines, uri
FROM Functions WHERE uri LIKE 'file:///src/Auth/%'
ORDER BY lines DESC LIMIT 10;
```

Find every implementation of an interface:
```sql
SELECT name, uri FROM Types WHERE implements LIKE '%IGraphReader%';
```

Files churned hardest by git over the indexed history:
```sql
SELECT uri, commits, authors, last_changed FROM git_hotspots
WHERE uri LIKE 'file:///src/Auth/%' ORDER BY commits DESC LIMIT 10;
```

Hybrid search + enrich — which files match a query, plus their metadata:
```sql
SELECT s.uri, s.score, f.content_category, f.token_count
FROM search_pipeline('jwt validation') s JOIN Files f ON s.uri = f.uri
LIMIT 10;
```

Lint summary — counts by rule across the project:
```sql
SELECT rule_id, severity, COUNT(*) FROM Annotations
GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 20;
```

Inline a small lookup table without leaving SQL:
```sql
SELECT f.uri, o.team FROM Files f
JOIN parse('pattern,team\n**/Auth/**,Security\n**/Core/**,Platform') o
ON f.uri LIKE o.pattern;
```

Learn the shape of an unfamiliar view:
```sql
SUMMARIZE Files;
```

Ask an LLM to synthesize across a query result (cheap fan-out):
```sql
SELECT ask(
  (SELECT json_group_array({'uri': uri, 'headline': headline})
   FROM Files WHERE uri LIKE 'file:///src/Auth/%'),
  'Summarize the responsibilities split across these files', 800);
```
</QUICK_PATTERNS>

<BUDGET>
Large results auto-summarize when they exceed the budget — pick a budget that reflects the maximum you'd be willing to spend on the result.

A failed query is cheap (one round-trip, no rows). Run `SUMMARIZE` or `EXPLAIN` first when you're guessing the shape.
</BUDGET>
