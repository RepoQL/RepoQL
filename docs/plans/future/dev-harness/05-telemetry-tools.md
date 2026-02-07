---
description: Fifth increment - logs and traces via Aspire MCP
tags: [dev-harness, plan, telemetry, logs, traces]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Telemetry Tools

Implements: [Dev Harness Design](../../designs/future/dev-harness.md) — harness.logs, harness.traces

## Scope

**Covers:**
- `harness.logs` tool — query structured logs from Aspire
- `harness.traces` tool — query traces from Aspire
- Filter translation to Aspire MCP parameters
- Crash context retrieval

**Does not cover:**
- Multi-session coordination (Plan 06)

## Architecture

```
Claude ──► Harness ──harness.logs()──► Aspire MCP ──► OpenTelemetry data
              │
              └──harness.traces()──► Aspire MCP ──► Trace data
```

Aspire collects OpenTelemetry data from the host. The harness queries it via Aspire's MCP tools.

## Enables

Once telemetry tools exist:
- **Debug in conversation** — No browser, no context switch
- **Crash investigation** — Query logs around crash time
- **Request tracing** — Follow requests through the system

## Prerequisites

- Plan 03 complete (Aspire client working)
- Aspire MCP exposes `list_structured_logs` and `list_traces` tools

## Done Criteria

### harness.logs Tool

- The `harness.logs` tool shall accept parameters:
  - `since`: Time window ("5m", "1h", ISO timestamp)
  - `level`: Minimum severity ("debug", "info", "warning", "error")
  - `contains`: Text search
  - `resource`: Resource name filter (default: "host")
  - `limit`: Max results (default: 100, max: 1000)
- The system shall translate parameters to Aspire `list_structured_logs` call
- The result shall include:
  ```json
  {
    "logs": [
      {
        "timestamp": "2026-02-05T14:30:01.234Z",
        "level": "error",
        "message": "Query failed: syntax error",
        "resource": "host",
        "attributes": { "sql": "SELECT * FORM..." }
      }
    ],
    "count": 1,
    "truncated": false
  }
  ```

### harness.traces Tool

- The `harness.traces` tool shall accept parameters:
  - `since`: Time window
  - `has_error`: Filter to traces with errors (boolean)
  - `resource`: Resource name filter
  - `limit`: Max results (default: 10, max: 100)
- The system shall translate parameters to Aspire `list_traces` call
- The result shall include trace hierarchy with spans

### Time Parsing

- When `since` is duration string ("5m", "1h", "30s"), parse as relative to now
- When `since` is ISO timestamp, parse as absolute time
- Invalid format shall return validation error

### Crash Context

- When `harness.logs({ crash_context: true })` is called after a crash:
  - Query logs from 30 seconds before last known operation
  - Include any error-level logs
  - Format for easy debugging

## Constraints

- **Read-only** — No log modification, only queries
- **Aspire as source** — Query Aspire directly, no local storage
- **Resource filtering** — Default to "host" resource to avoid noise

## Implementation Notes

- Aspire MCP tools: `list_structured_logs`, `list_traces`
- May need to handle pagination for large result sets
- Consider caching recent queries for repeated access

## Verification

1. Generate error in host, call `harness.logs({ level: "error" })`, verify error appears
2. Call `harness.traces({ has_error: true })`, verify error traces shown
3. Test time filters with various formats
