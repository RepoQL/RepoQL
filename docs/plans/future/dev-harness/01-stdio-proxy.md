---
description: First increment - harness as stdio proxy to repoql mcp subprocess
tags: [dev-harness, plan, mcp, proxy, stdio]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Stdio Proxy

Implements: [Dev Harness Design](../../designs/future/dev-harness.md) — Tool routing (proxy path)

## Scope

**Covers:**
- Harness as stdio MCP server (Claude connects here)
- Spawning `repoql mcp` as subprocess
- Bidirectional stdio forwarding (MCP JSON-RPC)
- Request/response correlation
- `_harness` metadata on proxied responses
- Graceful subprocess lifecycle

**Does not cover:**
- Harness-specific tools (Plan 02)
- Aspire integration (Plan 03)
- Build/deploy operations (Plan 04)
- Telemetry access (Plan 05)
- Multi-session coordination (Plan 06)

## Architecture

```
Claude Code ──stdio──► Dev Harness ──stdio──► repoql mcp ──gRPC──► host
             (stdin/    (this plan)           (subprocess)
              stdout)
```

The harness is a **transparent stdio proxy**:
1. Receives JSON-RPC messages from Claude on stdin
2. Forwards to `repoql mcp` subprocess stdin
3. Reads responses from subprocess stdout
4. Returns to Claude on stdout

This tests the real `repoql mcp` code path, including its gRPC client and reconnection logic.

## Enables

Once the stdio proxy exists:
- **Zero-risk insertion** — Can deploy and verify harness doesn't break anything
- **Request correlation** — All requests get IDs for future tracing
- **Foundation validated** — MCP hosting, subprocess management, forwarding proven
- **Plan 02** can proceed — has MCP shell to add harness tools

## Prerequisites

- `repoql mcp` command works standalone
- MCP JSON-RPC protocol understood (newline-delimited JSON)

## North Star

Invisible when working. Agent connects to harness instead of `repoql mcp` directly, notices no difference except `_harness` metadata on responses.

## Done Criteria

### MCP Server Setup

- When harness starts, the system shall spawn `repoql mcp` as a subprocess
- The harness shall expose an MCP interface on its own stdio (what Claude connects to)
- When harness receives `initialize` request, the system shall forward to subprocess and return response
- The harness shall pass through all tool definitions from subprocess

### Message Forwarding

- When a JSON-RPC message is received on harness stdin, the system shall forward it to subprocess stdin
- When a JSON-RPC message is received from subprocess stdout, the system shall forward it to harness stdout
- The system shall handle concurrent requests (MCP allows pipelining)
- The system shall preserve message ordering per JSON-RPC semantics

### Request Correlation

- The harness shall generate a unique request ID for each tool call: `req_{timestamp}_{4-char-random}`
- The harness shall track in-flight requests for correlation
- The harness shall measure duration from forward to response

### Response Enhancement

- When a tool call response is received from subprocess, the system shall inject `_harness` metadata:
  ```json
  {
    "...original response...": "...",
    "_harness": {
      "request_id": "req_20260205143022_b2c4",
      "duration_ms": 45
    }
  }
  ```
- The `_harness` metadata shall NOT be added to:
  - MCP protocol messages (initialize, notifications)
  - Error responses (they have their own structure)

### Subprocess Lifecycle

- When harness stdin closes (Claude disconnects), the system shall terminate subprocess gracefully
- When subprocess exits unexpectedly, the system shall log and exit harness with error
- When harness receives SIGTERM/SIGINT, the system shall terminate subprocess and exit cleanly
- The system shall propagate environment variables to subprocess (especially `REPOQL_CWD`)

### Error Handling

- When subprocess fails to start, the system shall exit with clear error message
- When subprocess crashes during operation, the system shall return error to any in-flight requests
- Stderr from subprocess shall be forwarded to harness stderr

## Constraints

- **Pure passthrough** — No message modification except `_harness` metadata injection
- **No tool interception yet** — All tools forwarded; harness tools come in Plan 02
- **Single subprocess** — One `repoql mcp` process per harness instance

## Implementation Notes

- Use `System.Diagnostics.Process` with redirected stdio
- JSON-RPC messages are newline-delimited JSON
- Must handle partial reads (buffering until newline)
- Consider using `System.IO.Pipelines` for efficient streaming

## Verification

1. Start harness, connect Claude Code to it
2. Run queries, verify results match direct `repoql mcp` connection
3. Check `_harness` metadata present on tool responses
4. Kill harness, verify subprocess terminates
5. Kill subprocess, verify harness exits with error
