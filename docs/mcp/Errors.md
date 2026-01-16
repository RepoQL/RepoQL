# MCP Errors

> JSON-RPC error codes and handling patterns

## Overview

MCP uses JSON-RPC 2.0 error handling. Errors are returned as structured responses with error codes, messages, and optional data.

## Error Response Format

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "reason": "Missing required argument: location"
    }
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `code` | Yes | Integer error code |
| `message` | Yes | Human-readable error message |
| `data` | No | Additional error details |

## Standard JSON-RPC Errors

These are defined by the JSON-RPC 2.0 specification:

| Code | Name | Description |
|------|------|-------------|
| -32700 | Parse error | Invalid JSON received |
| -32600 | Invalid Request | JSON is not a valid request object |
| -32601 | Method not found | Method does not exist or not available |
| -32602 | Invalid params | Invalid method parameters |
| -32603 | Internal error | Internal JSON-RPC error |
| -32000 to -32099 | Server error | Reserved for server-defined errors |

## MCP-Specific Errors

| Code | Name | Description |
|------|------|-------------|
| -32002 | Resource not found | Requested resource does not exist |
| -32001 | Request cancelled | Operation was cancelled |

## Error Examples by Category

### Parse Error (-32700)

```json
{
  "error": {
    "code": -32700,
    "message": "Parse error",
    "data": {
      "position": 42,
      "expected": "}"
    }
  }
}
```

### Invalid Request (-32600)

```json
{
  "error": {
    "code": -32600,
    "message": "Invalid Request",
    "data": {
      "reason": "Missing 'method' field"
    }
  }
}
```

### Method Not Found (-32601)

```json
{
  "error": {
    "code": -32601,
    "message": "Method not found",
    "data": {
      "method": "tools/execute",
      "suggestion": "Did you mean 'tools/call'?"
    }
  }
}
```

### Invalid Params (-32602)

```json
{
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "param": "uri",
      "reason": "Invalid URI format",
      "value": "not-a-valid-uri"
    }
  }
}
```

### Internal Error (-32603)

```json
{
  "error": {
    "code": -32603,
    "message": "Internal error",
    "data": {
      "reason": "Database connection failed"
    }
  }
}
```

### Resource Not Found (-32002)

```json
{
  "error": {
    "code": -32002,
    "message": "Resource not found",
    "data": {
      "uri": "file:///nonexistent.txt"
    }
  }
}
```

## Errors by Feature

### Lifecycle Errors

**Protocol version mismatch:**
```json
{
  "error": {
    "code": -32602,
    "message": "Unsupported protocol version",
    "data": {
      "supported": ["2025-11-25", "2025-06-18"],
      "requested": "2024-01-01"
    }
  }
}
```

**Capability not supported:**
```json
{
  "error": {
    "code": -32601,
    "message": "Roots not supported",
    "data": {
      "reason": "Client does not have roots capability"
    }
  }
}
```

### Tool Errors

**Unknown tool:**
```json
{
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "reason": "Unknown tool",
      "tool": "nonexistent_tool"
    }
  }
}
```

**Note:** Tool execution errors are returned in the result, not as JSON-RPC errors:
```json
{
  "result": {
    "content": [{ "type": "text", "text": "Error: API rate limit exceeded" }],
    "isError": true
  }
}
```

### Resource Errors

**Resource not found:**
```json
{
  "error": {
    "code": -32002,
    "message": "Resource not found",
    "data": {
      "uri": "file:///missing.txt"
    }
  }
}
```

**Invalid URI:**
```json
{
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "param": "uri",
      "reason": "Malformed URI",
      "value": ":::invalid:::"
    }
  }
}
```

### Prompt Errors

**Unknown prompt:**
```json
{
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "reason": "Unknown prompt",
      "prompt": "nonexistent_prompt"
    }
  }
}
```

**Missing required argument:**
```json
{
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "reason": "Missing required argument",
      "argument": "code"
    }
  }
}
```

### Sampling Errors

**Sampling not supported:**
```json
{
  "error": {
    "code": -32601,
    "message": "Sampling not supported",
    "data": {
      "reason": "Client does not have sampling capability"
    }
  }
}
```

**User declined:**
```json
{
  "error": {
    "code": -32603,
    "message": "Sampling request declined",
    "data": {
      "reason": "User declined the sampling request"
    }
  }
}
```

### Logging Errors

**Invalid log level:**
```json
{
  "error": {
    "code": -32602,
    "message": "Invalid params",
    "data": {
      "param": "level",
      "reason": "Unknown log level",
      "value": "verbose",
      "allowed": ["debug", "info", "notice", "warning", "error", "critical", "alert", "emergency"]
    }
  }
}
```

## HTTP Status Codes

For HTTP transports, JSON-RPC errors map to HTTP status codes:

| HTTP Status | JSON-RPC Codes | Usage |
|-------------|----------------|-------|
| 200 OK | Success or JSON-RPC error | Normal response (errors in body) |
| 400 Bad Request | -32700, -32600 | Malformed request |
| 401 Unauthorized | N/A | Authentication required |
| 403 Forbidden | N/A | Insufficient permissions |
| 404 Not Found | -32601 | Unknown endpoint |
| 500 Internal Server Error | -32603 | Server failure |

## Error Handling Best Practices

### For Servers

1. **Use appropriate error codes** - don't use -32603 for everything
2. **Provide helpful messages** - explain what went wrong
3. **Include relevant data** - help clients understand and fix issues
4. **Never expose sensitive info** - no stack traces or internal paths
5. **Log errors server-side** - for debugging and monitoring

### For Clients

1. **Handle all error codes** - at minimum, display the message
2. **Implement retry logic** - for transient errors (-32603)
3. **Don't retry** - for validation errors (-32602)
4. **Present errors clearly** - to users when appropriate
5. **Log errors client-side** - for debugging

### Error Data Patterns

**Validation errors:**
```json
{
  "data": {
    "param": "paramName",
    "reason": "Why it's invalid",
    "value": "The invalid value",
    "expected": "What was expected"
  }
}
```

**Not found errors:**
```json
{
  "data": {
    "type": "tool|resource|prompt",
    "identifier": "name or uri"
  }
}
```

**Capability errors:**
```json
{
  "data": {
    "capability": "sampling|roots|etc",
    "reason": "Why it's not available"
  }
}
```
