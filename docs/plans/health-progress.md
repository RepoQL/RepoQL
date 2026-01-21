# Plan: Health Progress

Implements: [Reliability Design](../designs/reliability.md) — Health Check section

## Scope

**Covers:**
- Health check extended with startup phase and progress
- Startup grace period (don't idle-shutdown during init)
- Progress reporting during indexing
- Stall detection
- Readiness gating

**Does not cover:**
- Service degradation reporting (Plan: Service Degradation)
- Diagnostic data collection (Plan: Diagnostics)
- Channel recovery (Plan: Connection Recovery)

## Enables

Once Health Progress exists:
- **Progress visibility** — "Indexing... 45% (1247/2771 files)" instead of just "not ready"
- **No premature timeout** — client waits for startup instead of failing
- **Stall detection** — "Stuck on large-file.json for 60 seconds"
- **No idle-shutdown race** — grace period protects slow startup

## Prerequisites

- gRPC health check service exists (already implemented)
- Indexer reports progress (existing or needs extension)

## North Star

When the host is starting up, the client sees exactly what's happening and how far along it is — not just "not ready" but "Indexing file 1247 of 2771 (45%)".

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

### Extended Status (Progress)

- For detailed progress, add a separate `GetStatus` RPC (not part of standard health check)
- GetStatus shall return:
  - Phase: `preflight`, `socket_bind`, `database_init`, `services_start`, `indexing`, `ready`
  - Progress percent (0-100) when indexing
  - Files total and completed when indexing
  - Current file when indexing
  - Uptime in seconds

### Phase Reporting

- Each startup phase shall update health status as it begins
- When entering `indexing` phase, report file counts
- When indexing progresses, update progress percent
- When indexing completes, transition to `ready`

### Startup Grace Period

- The host shall not idle-shutdown while phase != `ready`
- The startup grace period shall be configurable (default: 5 minutes)
- When startup exceeds grace period, log warning but don't force shutdown
- Idle timer starts only after phase = `ready`

### Progress Tracking

- The indexer shall report current file being processed
- The indexer shall report files completed vs total
- Progress updates shall occur at least every 5 seconds during active indexing
- The health check shall include timestamp of last progress update

### Stall Detection

- When progress unchanged for 60 seconds during indexing, flag as potentially stalled
- Include current file in stall warning (helps identify problematic files)
- Stall flag is informational, not an error (large files take time)

### Client Integration

- Client health check should display phase and progress
- When phase is `indexing`, show progress bar or percentage
- When progress stalled, show warning with current file

## Constraints

- **Don't block on health check** — return current state immediately, don't wait
- **Progress is best-effort** — some operations don't have progress (e.g., schema migration)
- **Grace period has limit** — eventually log concern, but don't force shutdown

## References

- [Reliability Design](../designs/reliability.md) — health check with progress decision
- [Readiness Flow](../flows/future/host/failure-modes/readiness.md) — detailed scenarios
- [gRPC Health Check](https://github.com/grpc/grpc/blob/master/doc/health-checking.md) — standard protocol

## Error Policy

Health check never fails — always returns current state:
1. If startup failed, return phase where it failed + error
2. If stalled, return stall info + current file
3. If degraded, return degraded services list
4. Always return something useful
