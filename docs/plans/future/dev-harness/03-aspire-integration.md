---
description: Third increment - Aspire MCP client for lifecycle control
tags: [dev-harness, plan, aspire, lifecycle]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Aspire Integration

Implements: [Dev Harness Design](../../designs/future/dev-harness.md) — Aspire connection, host state tracking

## Scope

**Covers:**
- HTTP client to Aspire MCP server (`http://localhost:18891`)
- `list_resources` for host state detection
- Host state tracking (`ready`, `stopped`, `unknown`)
- Connection resilience (auto-reconnect on Aspire restart)
- Real `harness.status` with host state from Aspire

**Does not cover:**
- Build/deploy/restart operations (Plan 04)
- Telemetry tools (Plan 05)
- Multi-session coordination (Plan 06)

## Architecture

```
Claude ──► Harness ──► repoql mcp ──► host
              │                         │
              └──HTTP──► Aspire MCP ────┘
                         (localhost:18891)
```

The harness queries Aspire to determine host state, providing accurate status to agents.

## Enables

Once Aspire integration exists:
- **Real host state** — Status reflects actual host health
- **Lifecycle foundation** — Can execute Aspire commands
- **Plan 04** can proceed — has Aspire client for rebuild/restart commands

## Prerequisites

- Plan 02 complete (harness tools working)
- Aspire dashboard running with MCP server enabled
- Aspire MCP endpoint at `http://localhost:18891`

## Done Criteria

### Aspire MCP Client

- The system shall connect to Aspire MCP at `http://localhost:18891` (or `ASPIRE_MCP_URL` env var)
- The system shall use HTTP streaming transport (Aspire's MCP uses streamable HTTP)
- The client shall handle connection failures gracefully

### Host State Detection

- The system shall call `list_resources` to enumerate Aspire resources
- The system shall find resource named "host" (the RepoQL host)
- The system shall determine state from resource status:
  - `Running` → `ready`
  - `Stopped` / `Exited` → `stopped`
  - Other / not found → `unknown`
- The system shall poll state periodically (every 5 seconds) or on-demand

### Connection Resilience

- When Aspire MCP is unreachable, the system shall set host state to `unknown`
- When Aspire restarts (connection lost), the system shall reconnect automatically
- The system shall log connection state changes

### Enhanced harness.status

- The `harness.status` tool shall now include real host state:
  ```json
  {
    "harness": {
      "session_id": "sess_abc123",
      "started_at": "2026-02-05T14:30:00Z",
      "subprocess_pid": 12345,
      "aspire_connected": true
    },
    "host": {
      "state": "ready",
      "resource_name": "host"
    }
  }
  ```

### State-Aware Routing

- When host state is `stopped` and a tool call is received:
  - Return error: `{ "error": "host_stopped", "message": "RepoQL host is not running. Use harness.restart() to start it." }`
- When host state is `unknown`:
  - Forward to subprocess anyway (let it handle connection errors)

## Constraints

- **Aspire MCP protocol** — Use standard MCP client over HTTP streaming
- **No lifecycle commands yet** — Only read state; commands in Plan 04
- **Subprocess still runs** — Even if host is stopped, subprocess stays alive (it will reconnect when host starts)

## Implementation Notes

- Aspire MCP uses HTTP streaming at `/mcp` endpoint
- `list_resources` returns resource state including health status
- Consider using existing MCP client library or implementing minimal client

## Verification

1. Start harness with Aspire running, verify `harness.status` shows `ready`
2. Stop host via Aspire dashboard, verify `harness.status` shows `stopped`
3. Stop Aspire, verify `harness.status` shows `aspire_connected: false`
4. Restart Aspire, verify reconnection
