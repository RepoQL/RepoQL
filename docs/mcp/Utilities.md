# MCP Utilities

> Supporting protocol features: cancellation, progress, ping, tasks, roots, elicitation

## Overview

MCP provides several utility features that support the core primitives. These features enable better control over long-running operations, connection health monitoring, and advanced client-server interactions.

## Cancellation

Allows either party to cancel an in-progress request.

### Cancellation Notification

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/cancelled",
  "params": {
    "requestId": "123",
    "reason": "User requested cancellation"
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `requestId` | Yes | ID of the request to cancel |
| `reason` | No | Human-readable cancellation reason |

### Behavior

- Cancellation is a **notification** (no response expected)
- Receiver **SHOULD** stop processing if possible
- Receiver **MAY** still return a result if already complete
- Sender **SHOULD** be prepared to receive results after cancellation

### Usage

```
Client                                    Server
   │                                         │
   │─── request (id: 123) ──────────────────▶│
   │                                         │ ← (processing)
   │─── notifications/cancelled ────────────▶│
   │    (requestId: 123)                     │
   │                                         │ ← (stops processing)
   │◀── error or result ─────────────────────│
```

## Progress

Reports progress for long-running operations.

### Progress Token

Requests can include a `progressToken` to enable progress reporting:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "analyze_codebase",
    "_meta": {
      "progressToken": "progress-123"
    }
  }
}
```

### Progress Notification

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/progress",
  "params": {
    "progressToken": "progress-123",
    "progress": 50,
    "total": 100,
    "message": "Analyzing file 50 of 100..."
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `progressToken` | Yes | Token from original request |
| `progress` | Yes | Current progress value (must increase) |
| `total` | No | Total expected value |
| `message` | No | Human-readable status |

### Progress Flow

```
Client                                    Server
   │                                         │
   │─── request (progressToken: abc) ───────▶│
   │                                         │
   │◀── notifications/progress (25/100) ─────│
   │◀── notifications/progress (50/100) ─────│
   │◀── notifications/progress (75/100) ─────│
   │◀── notifications/progress (100/100) ────│
   │                                         │
   │◀── result ──────────────────────────────│
```

## Ping

Connection health check mechanism.

### Ping Request

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "ping"
}
```

### Ping Response

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {}
}
```

### Usage

- Either party can send ping
- Useful for keep-alive in long-lived connections
- Can be sent during initialization phase

## Tasks (v2025-11-25)

Tasks provide tracking for background operations.

### Task-Augmented Requests

Any request can be augmented with a task for async tracking:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "long_running_analysis",
    "_meta": {
      "task": true
    }
  }
}
```

### Task Response

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "_meta": {
      "taskId": "task-abc-123"
    }
  }
}
```

### List Tasks

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tasks/list"
}
```

### Get Task Status

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tasks/get",
  "params": {
    "taskId": "task-abc-123"
  }
}
```

### Cancel Task

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "tasks/cancel",
  "params": {
    "taskId": "task-abc-123"
  }
}
```

### Task Capability

```json
{
  "capabilities": {
    "tasks": {
      "list": {},
      "cancel": {}
    }
  }
}
```

## Roots

Server can query client's filesystem boundaries.

### Capability

```json
{
  "capabilities": {
    "roots": {
      "listChanged": true
    }
  }
}
```

### List Roots (Server → Client)

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "roots/list"
}
```

### Response

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "roots": [
      {
        "uri": "file:///home/user/project",
        "name": "Project Root"
      },
      {
        "uri": "file:///home/user/libs",
        "name": "Libraries"
      }
    ]
  }
}
```

### Roots Changed Notification

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/roots/list_changed"
}
```

## Elicitation

Server can request user input through the client.

### Capability

```json
{
  "capabilities": {
    "elicitation": {}
  }
}
```

### Elicitation Request (Server → Client)

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "elicitation/create",
  "params": {
    "message": "Please enter your API key:",
    "schema": {
      "type": "object",
      "properties": {
        "apiKey": {
          "type": "string",
          "description": "Your API key"
        }
      },
      "required": ["apiKey"]
    }
  }
}
```

### Response

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "action": "accept",
    "content": {
      "apiKey": "sk-abc123..."
    }
  }
}
```

### Actions

| Action | Description |
|--------|-------------|
| `accept` | User provided input |
| `decline` | User declined to provide input |
| `cancel` | User cancelled the request |

## Logging

Servers can emit structured log messages.

### Capability

```json
{
  "capabilities": {
    "logging": {}
  }
}
```

### Set Log Level

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "logging/setLevel",
  "params": {
    "level": "info"
  }
}
```

### Log Notification

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/message",
  "params": {
    "level": "info",
    "logger": "server.tools",
    "data": {
      "message": "Tool execution started",
      "tool": "analyze_code"
    }
  }
}
```

### Log Levels

| Level | Description |
|-------|-------------|
| `debug` | Detailed debugging |
| `info` | General information |
| `notice` | Normal but significant |
| `warning` | Warning conditions |
| `error` | Error conditions |
| `critical` | Critical conditions |
| `alert` | Action must be taken |
| `emergency` | System unusable |

## Completions

Autocompletion for tool/prompt arguments.

### Completion Request

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "completion/complete",
  "params": {
    "ref": {
      "type": "ref/prompt",
      "name": "code_review"
    },
    "argument": {
      "name": "language",
      "value": "py"
    }
  }
}
```

### Response

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "completion": {
      "values": ["python", "pytorch"],
      "hasMore": false
    }
  }
}
```

### Reference Types

| Type | Description |
|------|-------------|
| `ref/prompt` | Prompt argument completion |
| `ref/resource` | Resource URI completion |
