---
description: Second increment - harness-specific tools and tool interception
tags: [dev-harness, plan, tools, routing]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Harness Tools

Implements: [Dev Harness Design](../../designs/future/dev-harness.md) — Tool routing, harness.status

## Scope

**Covers:**
- Tool call interception for `harness.*` prefix
- `harness.status` tool implementation
- Tool registration in MCP initialize response
- Session ID generation

**Does not cover:**
- Aspire integration (Plan 03)
- Build/deploy/restart tools (Plan 04)
- Telemetry tools (Plan 05)
- Multi-session coordination (Plan 06)

## Architecture

```
Claude ──► Harness ──► repoql mcp subprocess
              │
              ├── harness.status → handled locally
              └── explore/query/read → forwarded to subprocess
```

## Enables

Once harness tools exist:
- **Tool interception works** — Foundation for all harness functionality
- **Status visibility** — Agent can query harness state
- **Plan 03** can proceed — has tool routing to add Aspire-backed tools

## Prerequisites

- Plan 01 complete (stdio proxy working)

## Done Criteria

### Tool Interception

- When tool call has name starting with `harness.`, the system shall handle it locally (not forward)
- When tool call has any other name, the system shall forward to subprocess
- The routing decision shall be made before forwarding

### Tool Registration

- When responding to `initialize`, the harness shall merge its tools with subprocess tools
- Harness tools shall have `harness.` prefix
- Tool schemas shall be valid MCP tool definitions

### harness.status Tool

- The `harness.status` tool shall return:
  ```json
  {
    "harness": {
      "session_id": "sess_abc123",
      "started_at": "2026-02-05T14:30:00Z",
      "subprocess_pid": 12345
    },
    "host": {
      "state": "ready"
    }
  }
  ```
- The `session_id` shall be generated at harness startup: `sess_{timestamp}_{4-char-random}`
- The `host.state` shall be `ready` (stub until Plan 03 adds real state tracking)

### Response Format

- Harness tool responses shall include `_harness` metadata like proxied responses
- Error responses shall follow MCP error format

## Constraints

- **Stub implementations OK** — `harness.status` returns basic info; real state tracking in Plan 03
- **No Aspire yet** — Host state is assumed `ready` until Plan 03

## Verification

1. Call `harness.status`, verify response structure
2. Call RepoQL tools (explore, query), verify still forwarded correctly
3. Check harness tools appear in tool list from initialize
