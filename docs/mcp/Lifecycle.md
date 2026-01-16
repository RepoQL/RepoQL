# MCP Lifecycle

> Connection initialization, operation, and shutdown

## Overview

MCP defines a three-phase lifecycle for client-server connections:

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│ Initialize   │ ──▶ │   Operate    │ ──▶ │   Shutdown   │
└──────────────┘     └──────────────┘     └──────────────┘
```

## Phase 1: Initialization

The initialization phase **MUST** be the first interaction between client and server.

### Sequence

```
Client                                    Server
   │                                         │
   │─────── initialize ─────────────────────▶│
   │        (protocolVersion, capabilities,  │
   │         clientInfo)                     │
   │                                         │
   │◀────── result ──────────────────────────│
   │        (protocolVersion, capabilities,  │
   │         serverInfo, instructions)       │
   │                                         │
   │─────── notifications/initialized ──────▶│
   │                                         │
   │◀═══════ Operation Phase ═══════════════▶│
```

### Initialize Request

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-11-25",
    "capabilities": {
      "roots": { "listChanged": true },
      "sampling": {},
      "elicitation": {}
    },
    "clientInfo": {
      "name": "ExampleClient",
      "version": "1.0.0"
    }
  }
}
```

### Initialize Response

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": "2025-11-25",
    "capabilities": {
      "tools": { "listChanged": true },
      "resources": { "subscribe": true, "listChanged": true },
      "prompts": { "listChanged": true },
      "logging": {}
    },
    "serverInfo": {
      "name": "ExampleServer",
      "version": "1.0.0"
    },
    "instructions": "Optional usage instructions for the client"
  }
}
```

### Initialized Notification

After successful initialization, the client sends:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/initialized"
}
```

### Constraints

| Party | Before Response | After Initialized |
|-------|-----------------|-------------------|
| Client | Only `ping` allowed | Full operation |
| Server | Only `ping` and `logging` allowed | Full operation |

## Phase 2: Version Negotiation

Version negotiation occurs during initialization:

1. Client sends its **latest supported** protocol version
2. Server responds with:
   - **Same version** if supported
   - **Different version** if client's version not supported (should be server's latest)
3. Client **SHOULD** disconnect if server's version is unsupported

### HTTP Header Requirement

For HTTP transports, clients **MUST** include the protocol version header on all subsequent requests:

```http
MCP-Protocol-Version: 2025-11-25
```

## Phase 3: Capability Negotiation

Capabilities establish which optional features are available during the session.

### Client Capabilities

| Capability | Description |
|------------|-------------|
| `roots` | Provides filesystem roots to servers |
| `sampling` | Supports LLM sampling requests from servers |
| `elicitation` | Supports user input requests from servers |
| `tasks` | Supports task-augmented requests |
| `experimental` | Non-standard experimental features |

### Server Capabilities

| Capability | Description |
|------------|-------------|
| `tools` | Exposes callable functions |
| `resources` | Provides readable data sources |
| `prompts` | Offers prompt templates |
| `logging` | Emits structured log messages |
| `completions` | Supports argument autocompletion |
| `tasks` | Supports task-augmented requests |
| `experimental` | Non-standard experimental features |

### Sub-Capabilities

| Sub-Capability | Applies To | Description |
|----------------|------------|-------------|
| `listChanged` | tools, resources, prompts | Emits notifications when list changes |
| `subscribe` | resources only | Supports subscribing to individual resource changes |

## Phase 4: Operation

During operation, both parties **MUST**:

- Respect the negotiated protocol version
- Only use capabilities that were successfully negotiated
- Handle requests/responses according to JSON-RPC 2.0

## Phase 5: Shutdown

### stdio Transport

Client **SHOULD** initiate shutdown by:

1. Close input stream to server
2. Wait for server to exit
3. Send `SIGTERM` if server doesn't exit within reasonable time
4. Send `SIGKILL` if server doesn't exit after `SIGTERM`

Server **MAY** initiate shutdown by closing its output stream and exiting.

### HTTP Transport

Shutdown is indicated by closing the associated HTTP connection(s).

## Timeouts

Implementations **SHOULD**:

- Establish timeouts for all requests
- Issue [cancellation notification](Utilities.md#cancellation) when timeout expires
- Allow per-request timeout configuration
- Optionally reset timeout on progress notifications
- **MUST** enforce maximum timeout regardless of progress

## Error Handling

### Version Mismatch

```json
{
  "jsonrpc": "2.0",
  "id": 1,
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

### Common Initialization Errors

| Scenario | Handling |
|----------|----------|
| Version mismatch | Client disconnects, tries older version or reports error |
| Required capability missing | Client may disconnect or operate with reduced functionality |
| Timeout during initialization | Client disconnects and retries or reports error |
