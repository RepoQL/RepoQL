---
description: "mcp_tools() → server, tool, macro_name, description, example"
tags: ["mcp_tools", "mcp", "discovery", "integration"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# mcp_tools

List connected MCP servers and the SQL macros they expose.

## Capsule: McpTools

**Invariant**
`mcp_tools()` only shows tools from MCP servers that are currently connected.

**Example**
```sql
SELECT macro_name, example
FROM mcp_tools();
```
//BOUNDARY: Tools only appear after their MCP server connects.

**Depth**
- `macro_name` is the callable SQL name, typically `{server}_{tool}` with special characters normalized
- Use `mcp_tool_params()` immediately after discovery to inspect the exact callable shape
