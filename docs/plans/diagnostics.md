# Plan: Diagnostics

Implements: [Reliability Design](../designs/reliability.md) — Diagnostics section

## Scope

**Covers:**
- `DiagnosticReport` record with all diagnostic fields
- Fact collection (socket, host, health, channel, database, previous session)
- Problem identification via pattern matching
- `ToString()` output with facts and guidance
- Integration with `:diagnostics:` query

**Does not cover:**
- Host-side file logging (Plan: Host Persistence)
- Preflight validation in host startup (Plan: Preflight Validation)
- Channel reconnection logic (Plan: Connection Recovery)

## Enables

Once Diagnostics exists:
- **`:diagnostics:` query** returns structured, actionable information
- **Automatic error context** can include relevant diagnostic facts
- **Plan: Connection Recovery** can use diagnostic data to decide when to reconnect
- **Agents stop getting misled** — they see what actually went wrong

## Prerequisites

- Host writes to `host.log` file (Plan: Host Persistence)
- gRPC health check returns phase and progress (Plan: Health Progress)

## North Star

When something fails, the agent sees exactly what's wrong — not "connection failed" but "socket exists, not connectable, previous host crashed with OutOfMemoryException 2 minutes ago."

## Done Criteria

### DiagnosticReport

- The DiagnosticReport shall include environment facts (cwd, repo root, platform)
- The DiagnosticReport shall include socket state (path, exists, connectable)
- The DiagnosticReport shall include host state (PID, running, process name)
- The DiagnosticReport shall include overall health state (status, phase, progress)
- The DiagnosticReport shall include per-service health (embeddings, indexer, watcher, mcp)
- The DiagnosticReport shall include channel state (connectivity state)
- The DiagnosticReport shall include lease state (active, last heartbeat time, stream state)
- The DiagnosticReport shall include database state (exists, locked, lock holder)
- The DiagnosticReport shall include index state (phase, progress, files total, current file, phase start time)
- The DiagnosticReport shall include recent log lines (last 20 from host.log)
- The DiagnosticReport shall include identified problems as a list

### Fact Collection

- When collecting facts, the collector shall check socket existence via file system
- When socket exists, the collector shall attempt connection to test connectivity
- When connected, the collector shall call gRPC health Check for overall status ("")
- The collector shall call health Check for each service (embeddings, indexer, watcher, mcp)
- The collector shall read channel state from the cached gRPC channel
- The collector shall read lease state (is stream active, last heartbeat time)
- The collector shall check database lock status and identify lock holder process
- When `{repoRoot}/.repoql/host.log` exists, the collector shall read last 20 lines

### Problem Identification

- When socket doesn't exist, identify "Host not running"
- When socket exists but not connectable, identify "Stale socket"
- When health status is NotServing, identify "Host unhealthy" with phase
- When channel state is TransientFailure, identify "Channel stuck"
- When database is locked by external process, identify "Database locked" with process name
- When recent log lines contain ERROR and host not running, identify "Previous host crashed" with error from log
- When lease stream is faulted, identify "Lease expired" with last heartbeat time
- When last heartbeat > 60s ago, identify "Lease stale" with time since last heartbeat
- When index phase is "indexing" and progress unchanged for >60s, identify "Index stalled" with current file
- When index has errors, identify "Index errors" with count

### Output

- The ToString method shall render problems first, then facts, then guidance
- Each problem shall include observable facts (not guesses)
- Each problem with known recovery shall include guidance
- The output shall be readable in a terminal (no JSON unless requested)

## Constraints

- **No guessing causes** — show what we observed, not what we think happened
- **Fast by default** — fact collection should complete in under 1 second for automatic context
- **No network calls beyond health check** — diagnostics must work when things are broken

## References

- [Reliability Design](../designs/reliability.md) — DiagnosticReport structure, error message pattern
- [gRPC Health Check](https://github.com/grpc/grpc/blob/master/doc/health-checking.md) — standard protocol
- Existing `RepoQlHostHealthCheck.cs` — current health check implementation

## Error Policy

Diagnostic collection should never throw. When a probe fails:
1. Record the failure as a fact (e.g., "health check failed: timeout")
2. Continue collecting other facts
3. Include probe failures in the report
