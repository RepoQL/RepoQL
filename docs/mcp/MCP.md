# Model Context Protocol (MCP)

> Reference documentation for MCP protocol version `2025-11-25`

## Overview

The **Model Context Protocol (MCP)** is an open standard enabling seamless integration between LLM applications and external data sources, tools, and services. Created by Anthropic in November 2024, MCP provides a standardized way for AI systems to access contextual information.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Host Application                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐     │
│  │   Client    │  │   Client    │  │   Client    │     │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘     │
└─────────┼────────────────┼────────────────┼─────────────┘
          │                │                │
          ▼                ▼                ▼
    ┌──────────┐     ┌──────────┐     ┌──────────┐
    │  Server  │     │  Server  │     │  Server  │
    └──────────┘     └──────────┘     └──────────┘
```

| Role | Description |
|------|-------------|
| **Host** | LLM application that initiates connections (e.g., Claude Desktop, IDE) |
| **Client** | Protocol connector within the host, manages one server connection |
| **Server** | Service providing context, tools, resources, and prompts |

## Foundation

- **Message Format**: JSON-RPC 2.0
- **Inspiration**: Language Server Protocol (LSP)
- **Connections**: Stateful sessions with capability negotiation

## Protocol Features

### Server Features (Server → Client)

| Feature | Description | Document |
|---------|-------------|----------|
| [Tools](Tools.md) | Functions the AI model can execute | Required reading |
| [Resources](Resources.md) | Contextual data (files, schemas, etc.) | Required reading |
| [Prompts](Prompts.md) | Templated message sequences | Required reading |
| Logging | Structured diagnostic messages | See [Utilities](Utilities.md) |
| Completions | Argument autocompletion | See [Utilities](Utilities.md) |

### Client Features (Client → Server)

| Feature | Description | Document |
|---------|-------------|----------|
| [Sampling](Sampling.md) | Server-initiated LLM generation requests | |
| Roots | Filesystem boundary queries | See [Utilities](Utilities.md) |
| Elicitation | Server-initiated user input requests | See [Utilities](Utilities.md) |

### Infrastructure

| Topic | Description | Document |
|-------|-------------|----------|
| [Lifecycle](Lifecycle.md) | Connection initialization, operation, shutdown | |
| [Transports](Transports.md) | stdio, HTTP, SSE communication | |
| [Authorization](Authorization.md) | OAuth 2.1 for HTTP transports | |
| [Utilities](Utilities.md) | Cancellation, progress, ping, tasks | |
| [Errors](Errors.md) | JSON-RPC error codes and handling | |

## Quick Reference

### Capability Negotiation

**Client capabilities**: `roots`, `sampling`, `elicitation`, `tasks`, `experimental`

**Server capabilities**: `tools`, `resources`, `prompts`, `logging`, `completions`, `tasks`, `experimental`

### Common Methods

```
initialize                      # Start connection
notifications/initialized       # Ready signal

tools/list                      # List available tools
tools/call                      # Invoke a tool

resources/list                  # List available resources
resources/read                  # Read resource content
resources/subscribe             # Subscribe to changes

prompts/list                    # List available prompts
prompts/get                     # Get prompt with arguments

sampling/createMessage          # Request LLM generation (server→client)
```

### Message Structure

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "get_weather",
    "arguments": { "location": "Paris" }
  }
}
```

## Security Principles

1. **User Consent**: Explicit approval for all data access and tool invocation
2. **Tool Safety**: Tools execute arbitrary code - treat untrusted servers with caution
3. **Sampling Control**: Users approve LLM requests initiated by servers
4. **Transport Security**: HTTPS required for remote connections; localhost for local

## External Resources

- [Official Specification](https://modelcontextprotocol.io/specification/2025-11-25)
- [GitHub Repository](https://github.com/modelcontextprotocol/modelcontextprotocol)
- [TypeScript SDK](https://github.com/modelcontextprotocol/typescript-sdk)
- [Python SDK](https://github.com/modelcontextprotocol/python-sdk)
