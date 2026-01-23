# Plan: Health Progress

Implements: [Reliability Design](../designs/reliability.md) — Health Check section

## Scope

**Covers:**
- Readiness gating via standard gRPC health checks
- Stage-level health registration (pipeline + degradation)
- Progress visibility via existing status stream (no new trackers)

**Does not cover:**
- Service degradation reporting (Plan: Service Degradation)
- Diagnostic data collection (Plan: Diagnostics)
- Channel recovery (Plan: Connection Recovery)
- New server-side progress trackers or stall detection (deferred)
- New GetStatus RPC (use existing status stream)

## Enables

Once Health Progress exists:
- **Standard readiness** — clients use gRPC health Watch/Check for ready vs not-ready
- **Fewer band-aids** — progress comes from existing status events, not new monitors

## Prerequisites

- gRPC health check service exists (already implemented)
- Indexer reports progress (existing or needs extension)

## North Star

When the host is starting up, clients get a clear ready/not-ready signal via standard gRPC health checks and can pull richer progress detail from the existing status stream when needed.

## Done Criteria

### Per-Service Health Registration

- The host shall register health status for overall service ("")
- The host shall register health status for each degradable service:
  - `repoql.embeddings`
  - `repoql.indexer`
  - `repoql.watcher`
  - `repoql.mcp`
- When a service degrades, the host shall call `SetStatus(service, NOT_SERVING)`
- When a service recovers, the host shall call `SetStatus(service, SERVING)`

### Watch + Check

- The client shall use `Watch("")` for immediate state change notifications
- The client shall also call `Check("")` periodically (every 30s) with 5s timeout as liveness probe
- Watch catches active state changes; Check catches deadlocks/hangs (host can't push if stuck)
- When Watch stream receives SERVING, the client shall consider host ready
- When Watch stream receives NOT_SERVING:
  - Log warning with service name
  - If overall health (""), collect diagnostic facts for context on next error
  - Do NOT block or fail requests immediately (host may recover)
- When Watch stream errors (connection lost), trigger channel recovery
- When Check times out, trigger channel recovery (host unresponsive/deadlocked)

### Readiness Gating

- Base service health ("" and `repoql.v1.RepoQL`) is NOT_SERVING until initial indexing barrier completes.
- Once the barrier completes, the service switches to SERVING.
- Per-stage and per-service health continues to flow via `PipelineHealthPublisher` and service degradation tracker.

### Progress Visibility (No New Tracker)

- Clients rely on `WatchStatus` (stream) and `GetPipelineStatus` (poll) for progress context.
- No new server-side monitor/snapshotter is added for progress.

### Idle Shutdown Behavior

- Implicit starts launched by MCP skip idle grace (shutdown immediately when leases drop to zero).
- CLI implicit starts keep the existing grace to avoid churn during quick successive commands.

### Stall Detection (Deferred)

- Explicit stall detection is deferred until we can implement it holistically.
- Note in client UX as a future enhancement (derived from repeated status snapshots).

### Client Integration

- Client health check uses gRPC health for readiness.
- Richer progress comes from `WatchStatus`/`GetPipelineStatus` (no new RPC required).

## Constraints

- **Don't block on health check** — return current state immediately, don't wait
- **Progress is best-effort** — use existing pipeline status; avoid new monitors

## References

- [Reliability Design](../designs/reliability.md) — health check with progress decision
- [Readiness Flow](../flows/future/host/failure-modes/readiness.md) — detailed scenarios
- [gRPC Health Check](https://github.com/grpc/grpc/blob/master/doc/health-checking.md) — standard protocol

## Error Policy

Health check never fails — always returns current state:
1. If not ready, return NOT_SERVING for base service ("" and `repoql.v1.RepoQL`).
2. If degraded, return NOT_SERVING for the relevant per-service health keys.
3. For detail, use `WatchStatus`/`GetPipelineStatus` rather than extending health payloads.
