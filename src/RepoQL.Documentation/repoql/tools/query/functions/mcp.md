---
description: "MCP tool integration: discover servers, call tools, parse structured responses into queryable rows"
tags: ["mcp", "mcp_tools", "mcp_tool_params", "parse", "parse_structured", "external", "integration"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# MCP Tools

Call external MCP servers and query results with SQL. Tools auto-register as table macros.

---

## Capsule: McpDiscovery

**Invariant**
`mcp_tools()` lists available tools; `mcp_tool_params()` lists their parameters with documentation.

**Example**
```sql
SELECT macro_name, example FROM mcp_tools();
SELECT param_name, param_type, required, description FROM mcp_tool_params() WHERE macro_name = 'context7_resolve_library_id';
```
//BOUNDARY: Tools only appear after their MCP server connects.

**Depth**
- `mcp_tools()` columns: server, tool, macro_name, description, example
- `mcp_tool_params()` columns: server, macro_name, param_name, original_name, param_type, required, description, default_value
- Filter by server: `WHERE server = 'context7'`

---

## Capsule: McpCalling

**Invariant**
Each MCP tool becomes a table macro; call with named parameters using `:=` syntax.

**Example**
```sql
SELECT * FROM context7_resolve_library_id(libraryname := 'react', query := 'hooks');
SELECT * FROM github_list_issues(state := 'open', labels := 'bug');
```
//BOUNDARY: Parameter names are lowercased; check `mcp_tool_params()` for exact names.

**Depth**
- Parameterless tools: `SELECT * FROM server_tool_name()`
- Required params must be provided; optional params default to NULL
- Macro names: `{server}_{tool}` with special chars replaced by underscores

---

## Capsule: McpResults

**Invariant**
MCP tools return rows with a `value` column containing JSON; extract fields with `json_extract`.

**Example**
```sql
SELECT
    json_extract_string(value, '$.title') AS title,
    json_extract(value, '$.score')::DOUBLE AS score
FROM context7_resolve_library_id(libraryname := 'duckdb', query := 'python')
WHERE json_extract_string(value, '$.library_id') LIKE '/%';
```
//BOUNDARY: Use `json_extract_string` for text, `json_extract` with cast for numbers.

**Depth**
- Each row is one item from the response
- Numbers in JSON are properly typed; cast with `::INTEGER` or `::DOUBLE`
- Filter with `WHERE json_extract_string(value, '$.field') = 'value'`

---

## Capsule: Parse

**Invariant**
`parse(text)` detects format and returns rows; handles JSON, JSONL, CSV, TSV, YAML, and embedded data.

**Example**
```sql
SELECT * FROM parse('id,name,score
1,Alice,95
2,Bob,87');

SELECT * FROM parse('server: localhost
port: 8080
debug: true');
```
//BOUNDARY: CSV/TSV require 2+ columns and 2+ data rows to avoid false positives on prose.

**Depth**
- Detection order: JSON → JSONL → TSV → CSV → YAML → Embedded → Structured text
- Type inference: numbers, booleans, floats auto-detected
- Structured text: `- Key: Value` format with `----------` delimiters
- Scalar version: `parse_structured(text)` returns JSON string

---

## Capsule: McpJoins

**Invariant**
Join MCP results with indexed repository data using standard SQL joins.

**Example**
```sql
SELECT f.uri, json_extract_string(i.value, '$.title') AS issue
FROM Files f
JOIN github_list_issues() i
  ON f.uri LIKE '%' || json_extract_string(i.value, '$.file') || '%';
```
//BOUNDARY: MCP calls execute once per query; results cached for the join.

**Depth**
- Cross-reference external data with code, docs, annotations
- Use CTE to name MCP results for cleaner queries
- Filter MCP results before joining for performance

---

## Common Patterns

| Goal | Query |
|------|-------|
| List all tools | `SELECT macro_name, example FROM mcp_tools()` |
| Tool parameters | `SELECT * FROM mcp_tool_params() WHERE macro_name = '...'` |
| Call with params | `SELECT * FROM tool(param := 'value')` |
| Extract JSON field | `json_extract_string(value, '$.field')` |
| Extract as number | `json_extract(value, '$.num')::DOUBLE` |
| Filter results | `WHERE json_extract_string(value, '$.status') = 'active'` |
| Parse CSV text | `SELECT * FROM parse('col1,col2\nval1,val2\nval3,val4')` |
| Parse YAML | `SELECT * FROM parse('key: value\nkey2: value2')` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| `tool(Param := 'x')` | Lowercase: `tool(param := 'x')` |
| `json_extract(value, 'field')` | Use path: `json_extract(value, '$.field')` |
| Expecting typed columns | Results are JSON; cast explicitly |
| Missing required param | Check `mcp_tool_params()` for required=true |
| Tool not found | Server may not be connected; check `mcp_tools()` |
