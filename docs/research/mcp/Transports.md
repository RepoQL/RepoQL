# MCP Transports

> Communication mechanisms for MCP client-server connections

## Overview

MCP supports multiple transport mechanisms for client-server communication. All transports carry JSON-RPC 2.0 messages.

| Transport | Use Case | Bidirectional | Streaming |
|-----------|----------|---------------|-----------|
| [stdio](#stdio) | Local processes | Yes | No |
| [Streamable HTTP](#streamable-http) | Remote servers | Yes | Optional (SSE) |
| [HTTP + SSE](#http-with-sse) | Remote servers (legacy) | Yes | Yes |

## stdio

Standard input/output transport for local subprocess communication.

### Characteristics

- Client spawns server as child process
- Messages sent via stdin/stdout
- Stderr available for logging
- Simple, no network configuration

### Message Flow

```
┌────────────┐          ┌────────────┐
│   Client   │          │   Server   │
│            │──stdin──▶│            │
│            │◀─stdout──│            │
│            │◀─stderr──│ (logging)  │
└────────────┘          └────────────┘
```

### Message Format

Messages are newline-delimited JSON:

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}\n
{"jsonrpc":"2.0","id":1,"result":{...}}\n
```

### Shutdown Sequence

1. Client closes stdin to server
2. Wait for server to exit gracefully
3. Send `SIGTERM` if timeout exceeded
4. Send `SIGKILL` if still running

## Streamable HTTP

HTTP-based transport supporting both synchronous requests and optional SSE streaming.

### Characteristics

- Single endpoint for all operations
- Supports POST (requests) and GET (SSE stream)
- Optional Server-Sent Events for server→client messages
- Suitable for remote deployments

### Endpoint

A single HTTP endpoint handles all MCP traffic:

```
https://example.com/mcp
```

### POST Request (Client → Server)

```http
POST /mcp HTTP/1.1
Host: example.com
Content-Type: application/json
MCP-Protocol-Version: 2025-11-25
Authorization: Bearer <token>

{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/list",
  "params": {}
}
```

### Response (Synchronous)

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "tools": [...]
  }
}
```

### Response (Streaming via SSE)

Server may respond with SSE stream for long-running operations:

```http
HTTP/1.1 200 OK
Content-Type: text/event-stream

event: message
data: {"jsonrpc":"2.0","method":"notifications/progress","params":{...}}

event: message
data: {"jsonrpc":"2.0","id":1,"result":{...}}
```

### GET Request (SSE Stream)

Client can open persistent SSE connection for server-initiated messages:

```http
GET /mcp HTTP/1.1
Host: example.com
Accept: text/event-stream
MCP-Protocol-Version: 2025-11-25
Authorization: Bearer <token>
```

## HTTP with SSE

Legacy transport using separate endpoints for requests and server events.

### Characteristics

- SSE endpoint for server→client messages
- POST endpoint for client→server messages
- Always streaming (no synchronous option)

### SSE Endpoint

Client connects to SSE endpoint to receive messages:

```http
GET / HTTP/1.1
Host: example.com
Accept: text/event-stream
```

Server sends endpoint URI for posting messages:

```
event: endpoint
data: "http://example.com/send"
```

### Message Sending

Client sends messages to the endpoint provided:

```http
POST /send HTTP/1.1
Host: example.com
Content-Type: application/json

{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {...}
}
```

### SSE Message Events

```
event: message
data: {"jsonrpc":"2.0","id":1,"result":{...}}

event: message
data: {"jsonrpc":"2.0","method":"notifications/resources/updated","params":{...}}
```

## Security Requirements

### All Transports

- Validate all incoming messages
- Handle malformed JSON gracefully
- Implement timeouts for requests

### HTTP Transports

| Requirement | Description |
|-------------|-------------|
| **Origin validation** | Servers **MUST** validate `Origin` header |
| **Localhost binding** | Local servers **SHOULD** bind only to `127.0.0.1` |
| **HTTPS** | Remote servers **MUST** use HTTPS |
| **Authentication** | Servers **SHOULD** implement proper authentication |

### Origin Header

```http
Origin: https://trusted-client.example.com
```

Servers should maintain an allowlist of trusted origins.

## Implementation Notes

### Choosing a Transport

| Scenario | Recommended Transport |
|----------|----------------------|
| Local CLI tool | stdio |
| IDE integration | stdio |
| Cloud service | Streamable HTTP |
| Browser-based client | Streamable HTTP |
| Legacy integration | HTTP + SSE |

### Connection Management

- Implement reconnection logic for HTTP transports
- Handle connection drops gracefully
- Use heartbeat/ping for connection health monitoring
