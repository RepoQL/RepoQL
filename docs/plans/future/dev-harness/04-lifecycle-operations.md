---
description: Fourth increment - build, deploy, restart operations via Aspire
tags: [dev-harness, plan, build, deploy, lifecycle]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Lifecycle Operations

Implements: [Dev Harness Design](../../designs/future/dev-harness.md) — harness.build, harness.deploy, harness.restart

## Scope

**Covers:**
- `harness.build` tool — rebuild and restart host
- `harness.deploy` tool — publish and deploy host
- `harness.restart` tool — restart host without rebuild
- Aspire `execute_resource_command` integration
- Operation state tracking (building, deploying)
- Subprocess reconnection after host restart

**Does not cover:**
- Telemetry tools (Plan 05)
- Multi-session coordination (Plan 06)

## Architecture

```
Claude ──► Harness ──► repoql mcp ──gRPC──► host
              │                              │
              └──Aspire commands─────────────┘
                 (rebuild_and_restart, stop, start)
```

When rebuild is triggered:
1. Harness calls Aspire command
2. Aspire stops host, builds, restarts host
3. `repoql mcp` subprocess reconnects to new host (existing resilient client)
4. Harness returns result to Claude

## Enables

Once lifecycle operations exist:
- **Iteration loop** — Agent can modify code and rebuild
- **Self-improvement** — RepoQL can be developed using RepoQL
- **Plan 05** can proceed — has lifecycle awareness for crash attribution

## Prerequisites

- Plan 03 complete (Aspire client working)
- Aspire `rebuild_and_restart` command registered (already exists in Orchestrator)

## Done Criteria

### harness.build Tool

- When `harness.build()` is called, the system shall:
  1. Set operation state to `building`
  2. Call Aspire `execute_resource_command` with `rebuild_and_restart`
  3. Wait for command completion
  4. Set operation state back to `ready` (or `crashed` on failure)
  5. Return result
- The result shall include:
  ```json
  {
    "success": true,
    "duration_ms": 15000,
    "message": "Build and restart completed successfully"
  }
  ```
- On failure:
  ```json
  {
    "success": false,
    "error": "Build failed (exit code 1): error CS1002...",
    "duration_ms": 5000
  }
  ```

### harness.restart Tool

- When `harness.restart()` is called, the system shall:
  1. Call Aspire `execute_resource_command` with `stop` on "host"
  2. Call Aspire `execute_resource_command` with `start` on "host"
  3. Wait for host to become ready
  4. Return result
- This is faster than build — just restarts existing code

### harness.deploy Tool

- When `harness.deploy()` is called, the system shall:
  1. Set operation state to `deploying`
  2. Run `dotnet publish` locally (harness runs this, not Aspire)
  3. Copy artifacts to deploy location
  4. Call Aspire restart
  5. Return result
- Deploy is for when you need a clean publish, not just incremental build

### Operation State

- The harness shall track current operation: `none`, `building`, `deploying`
- The `harness.status` shall include `current_operation` field
- When operation in progress, tool calls shall return:
  ```json
  {
    "error": "operation_in_progress",
    "operation": "building",
    "message": "Build in progress. Please wait.",
    "started_at": "2026-02-05T14:30:00Z"
  }
  ```

### Subprocess Handling

- The subprocess (`repoql mcp`) shall NOT be restarted during build
- The subprocess's resilient client will reconnect when host restarts
- If subprocess dies during build, harness shall restart it after build completes

## Constraints

- **No concurrent operations** — One build/deploy at a time
- **Subprocess stays alive** — Only host restarts, not the MCP subprocess
- **Configuration always Debug** — Simplification for dev harness

## Verification

1. Make code change, call `harness.build()`, verify change takes effect
2. Introduce compile error, call `harness.build()`, verify error returned
3. Call `harness.restart()`, verify faster than build
4. During build, call a tool, verify `operation_in_progress` error
