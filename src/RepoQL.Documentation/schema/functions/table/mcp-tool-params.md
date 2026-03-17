---
description: "mcp_tool_params() → server, macro_name, param_name, param_type, required, description"
tags: ["mcp_tool_params", "mcp", "parameters", "discovery"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# mcp_tool_params

List MCP tool parameters with documentation.

## Capsule: McpToolParams

**Invariant**
`mcp_tool_params()` exposes the queryable parameter contract for every connected MCP tool.

**Example**
```sql
SELECT param_name, param_type, required, description
FROM mcp_tool_params()
WHERE macro_name = 'context7_resolve_library_id';
```
//BOUNDARY: Tools only appear after their MCP server connects.

**Depth**
- Rows include `original_name` and `default_value` in addition to the headline columns
- Parameter names are lowercased for SQL macro calls
