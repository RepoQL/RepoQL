# MCP Tools

> Functions that AI models can discover and execute

## Overview

Tools are the primary mechanism for MCP servers to expose executable functionality to AI models. Each tool represents a function that can be invoked with specific arguments and returns structured results.

**Key principle**: Tools represent arbitrary code execution. Clients should treat tool descriptions from untrusted servers with caution.

## Capability Declaration

Servers supporting tools **MUST** declare the capability during initialization:

```json
{
  "capabilities": {
    "tools": {
      "listChanged": true
    }
  }
}
```

| Sub-capability | Description |
|----------------|-------------|
| `listChanged` | Server emits `notifications/tools/list_changed` when tools change |

## Tool Definition

```json
{
  "name": "get_weather",
  "title": "Weather Lookup",
  "description": "Get current weather information for a location",
  "inputSchema": {
    "type": "object",
    "properties": {
      "location": {
        "type": "string",
        "description": "City name or zip code"
      },
      "units": {
        "type": "string",
        "enum": ["celsius", "fahrenheit"],
        "default": "celsius"
      }
    },
    "required": ["location"]
  },
  "outputSchema": {
    "type": "object",
    "properties": {
      "temperature": { "type": "number" },
      "conditions": { "type": "string" }
    }
  },
  "annotations": {
    "title": "Weather Lookup",
    "readOnlyHint": true,
    "destructiveHint": false,
    "idempotentHint": true,
    "openWorldHint": true
  }
}
```

### Fields

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Unique identifier for the tool |
| `title` | No | Human-readable display name |
| `description` | No | What the tool does (shown to AI) |
| `inputSchema` | Yes | JSON Schema defining expected arguments |
| `outputSchema` | No | JSON Schema defining return value structure |
| `annotations` | No | Behavioral hints for clients |

### Tool Annotations

Annotations are **hints** about tool behavior. Clients should not rely solely on these for security decisions.

| Annotation | Type | Default | Description |
|------------|------|---------|-------------|
| `readOnlyHint` | boolean | false | Tool only reads data, no side effects |
| `destructiveHint` | boolean | true | Tool may perform destructive operations |
| `idempotentHint` | boolean | false | Repeated calls with same args have no additional effect |
| `openWorldHint` | boolean | - | Tool interacts with external systems |
| `title` | string | - | Alternative display name |

## Protocol Methods

### List Tools

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/list",
  "params": {
    "cursor": "optional-pagination-cursor"
  }
}
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "tools": [
      {
        "name": "get_weather",
        "description": "Get current weather for a location",
        "inputSchema": {...}
      },
      {
        "name": "search_files",
        "description": "Search for files matching a pattern",
        "inputSchema": {...}
      }
    ],
    "nextCursor": "next-page-cursor"
  }
}
```

### Call Tool

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "get_weather",
    "arguments": {
      "location": "Paris",
      "units": "celsius"
    }
  }
}
```

**Success Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "Current weather in Paris: 18°C, partly cloudy"
      }
    ],
    "isError": false
  }
}
```

**Error Response (tool execution failed):**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "Failed to fetch weather: API rate limit exceeded"
      }
    ],
    "isError": true
  }
}
```

### List Changed Notification

When tools change, servers emit:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/tools/list_changed"
}
```

Clients should re-fetch the tool list upon receiving this notification.

## Content Types

Tool results can include multiple content types:

### Text Content

```json
{
  "type": "text",
  "text": "Result text here"
}
```

### Image Content

```json
{
  "type": "image",
  "data": "base64-encoded-image-data",
  "mimeType": "image/png"
}
```

### Embedded Resource

```json
{
  "type": "resource",
  "resource": {
    "uri": "file:///path/to/result.json",
    "mimeType": "application/json",
    "text": "{\"key\": \"value\"}"
  }
}
```

## Error Handling

### JSON-RPC Errors

| Code | Meaning | When |
|------|---------|------|
| -32602 | Invalid params | Missing required arguments, invalid tool name |
| -32603 | Internal error | Server-side failure |

### Tool Execution Errors

Tool execution errors are returned in the result with `isError: true`, not as JSON-RPC errors:

```json
{
  "result": {
    "content": [{ "type": "text", "text": "Error message" }],
    "isError": true
  }
}
```

## Security Considerations

1. **Validate all inputs** before executing tool logic
2. **Sanitize outputs** to prevent injection attacks
3. **Implement rate limiting** for expensive operations
4. **Log tool invocations** for audit purposes
5. **Require user confirmation** for destructive operations (client-side)

## Best Practices

### Tool Design

- Use clear, descriptive names (`search_files` not `sf`)
- Provide detailed descriptions for AI consumption
- Define comprehensive input schemas with descriptions
- Use appropriate annotations to signal behavior
- Return structured data when possible

### Input Schema

```json
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "Search query - supports wildcards (* and ?)",
      "minLength": 1,
      "maxLength": 1000
    },
    "limit": {
      "type": "integer",
      "description": "Maximum results to return",
      "default": 10,
      "minimum": 1,
      "maximum": 100
    }
  },
  "required": ["query"]
}
```
