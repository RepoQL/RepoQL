# MCP Resources

> Contextual data exposed by servers for AI consumption

## Overview

Resources allow MCP servers to expose data that provides context to language models. Unlike tools (which execute actions), resources represent readable data sources such as files, database schemas, API responses, or application-specific information.

Each resource is uniquely identified by a **URI**.

## Capability Declaration

Servers supporting resources **MUST** declare the capability during initialization:

```json
{
  "capabilities": {
    "resources": {
      "subscribe": true,
      "listChanged": true
    }
  }
}
```

| Sub-capability | Description |
|----------------|-------------|
| `subscribe` | Clients can subscribe to individual resource changes |
| `listChanged` | Server emits notifications when resource list changes |

## Resource Definition

```json
{
  "uri": "file:///project/src/main.rs",
  "name": "main.rs",
  "title": "Application Entry Point",
  "description": "Primary Rust application entry point",
  "mimeType": "text/x-rust",
  "size": 2048,
  "annotations": {
    "audience": ["assistant"],
    "priority": 0.8,
    "lastModified": "2025-01-15T10:30:00Z"
  }
}
```

### Fields

| Field | Required | Description |
|-------|----------|-------------|
| `uri` | Yes | Unique identifier (RFC 3986 compliant) |
| `name` | Yes | Short display name |
| `title` | No | Human-readable title |
| `description` | No | What the resource contains |
| `mimeType` | No | Content type (e.g., `text/plain`, `application/json`) |
| `size` | No | Size in bytes |
| `annotations` | No | Usage hints |

### Resource Annotations

| Annotation | Type | Description |
|------------|------|-------------|
| `audience` | string[] | Who should see this: `"user"`, `"assistant"`, or both |
| `priority` | number | Importance 0.0-1.0 (higher = more important) |
| `lastModified` | string | ISO 8601 timestamp |

## Protocol Methods

### List Resources

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "resources/list",
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
    "resources": [
      {
        "uri": "file:///project/src/main.rs",
        "name": "main.rs",
        "mimeType": "text/x-rust"
      },
      {
        "uri": "file:///project/README.md",
        "name": "README.md",
        "mimeType": "text/markdown"
      }
    ],
    "nextCursor": "next-page-cursor"
  }
}
```

### Read Resource

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "resources/read",
  "params": {
    "uri": "file:///project/src/main.rs"
  }
}
```

**Response (Text Content):**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "contents": [
      {
        "uri": "file:///project/src/main.rs",
        "mimeType": "text/x-rust",
        "text": "fn main() {\n    println!(\"Hello, world!\");\n}"
      }
    ]
  }
}
```

**Response (Binary Content):**

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "contents": [
      {
        "uri": "file:///project/logo.png",
        "mimeType": "image/png",
        "blob": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
      }
    ]
  }
}
```

### Resource Templates

Parameterized resources using URI templates (RFC 6570):

**Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "resources/templates/list"
}
```

**Response:**

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "result": {
    "resourceTemplates": [
      {
        "uriTemplate": "file:///{path}",
        "name": "Project Files",
        "description": "Access any file in the project directory",
        "mimeType": "application/octet-stream"
      },
      {
        "uriTemplate": "db:///{table}/schema",
        "name": "Table Schema",
        "description": "Get schema for a database table"
      }
    ]
  }
}
```

### Subscriptions

**Subscribe Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "resources/subscribe",
  "params": {
    "uri": "file:///project/config.json"
  }
}
```

**Unsubscribe Request:**

```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "method": "resources/unsubscribe",
  "params": {
    "uri": "file:///project/config.json"
  }
}
```

**Update Notification (server → client):**

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/resources/updated",
  "params": {
    "uri": "file:///project/config.json"
  }
}
```

### List Changed Notification

When the available resources change:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/resources/list_changed"
}
```

## Content Types

### Text Content

```json
{
  "uri": "file:///example.txt",
  "mimeType": "text/plain",
  "text": "File content as UTF-8 string"
}
```

### Binary Content

```json
{
  "uri": "file:///example.png",
  "mimeType": "image/png",
  "blob": "base64-encoded-binary-data"
}
```

## Common URI Schemes

| Scheme | Purpose | Example |
|--------|---------|---------|
| `file://` | Filesystem-like resources | `file:///src/main.rs` |
| `https://` | Web resources | `https://api.example.com/schema` |
| `git://` | Version control | `git://repo/branch/file` |
| `db://` | Database objects | `db:///users/schema` |
| Custom | Application-specific | `myapp://config/settings` |

**Note**: `file://` URIs don't require actual filesystem mapping - they're logical identifiers.

## Error Handling

| Code | Meaning | When |
|------|---------|------|
| -32002 | Resource not found | URI doesn't exist |
| -32602 | Invalid params | Malformed URI |
| -32603 | Internal error | Server-side failure |

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "error": {
    "code": -32002,
    "message": "Resource not found",
    "data": {
      "uri": "file:///nonexistent.txt"
    }
  }
}
```

## Security Considerations

1. **Validate all URIs** before processing
2. **Implement access controls** for sensitive resources
3. **Sanitize file paths** to prevent directory traversal
4. **Properly encode binary data** (base64)
5. **Check permissions** before exposing resources

## Best Practices

### Resource Design

- Use descriptive, consistent URI schemes
- Provide accurate MIME types
- Include helpful descriptions
- Set appropriate annotations for AI context selection
- Support pagination for large resource lists

### URI Design

```
scheme://authority/path?query#fragment

Examples:
file:///project/src/auth/login.ts
db:///users/schema
api:///v2/endpoints
config:///app/settings
```
