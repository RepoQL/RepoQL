# Plan: Host Takeover

Implements: [Reliability Design](../designs/reliability.md) — Host Startup section

## Scope

**Covers:**
- Detection of existing host via socket
- Graceful shutdown request via RPC
- Forceful process termination as fallback
- Stale socket cleanup
- Socket binding with platform-specific handling

**Does not cover:**
- Preflight path validation (Plan: Preflight Validation)
- Database opening and lock handling (Plan: Database Init)
- Service startup (Plan: Service Degradation)

## Enables

Once Host Takeover exists:
- **Clean handoff** between host instances
- **No orphaned hosts** consuming resources
- **Stale sockets cleaned up** automatically
- **Platform edge cases handled** (AF_UNIX probe errors, macOS path limits)

## Prerequisites

- Socket path determined (from preflight or REPOQL_SOCKET)
- gRPC Shutdown RPC exists on host

## North Star

When a new host starts, any existing host shuts down cleanly. No zombie processes, no stale sockets, no "address already in use" errors.

## Done Criteria

### Existing Host Detection

- The startup shall check if socket file exists
- When socket exists, attempt AF_UNIX connection probe
- When probe succeeds, existing host is running
- When probe fails with connection refused, socket is stale
- When probe fails with other error, log and treat as stale

### Graceful Shutdown

- When existing host detected, send Shutdown RPC with 5 second timeout
- When Shutdown RPC succeeds, wait up to 5 seconds for process exit
- When Shutdown RPC times out, escalate to forceful termination
- When Shutdown RPC fails (connection error), escalate to forceful termination

### Forceful Termination

- Read PID from `.repoql/host.pid` if exists
- When PID found and process is repoql, send SIGTERM (Unix) or TerminateProcess (Windows)
- Wait up to 3 seconds for process exit
- When process still running, send SIGKILL (Unix only)
- When PID not found or process not repoql, skip kill (don't kill unrelated processes)

### Stale Socket Cleanup

- When socket exists but no host running, delete the socket file
- When socket deletion fails (permissions), report error with path and guidance

### Socket Binding

- Normalize socket path (backslash → forward slash on Windows)
- Bind to socket path after cleanup
- When bind fails with "address in use", report that cleanup failed
- When bind fails with "path too long", report path length and platform limit (should be caught in preflight, but defensive)
- When bind fails with permission error, report path and suggest checking directory permissions

### PID File

- Write current PID to `.repoql/host.pid` after successful bind
- Delete PID file on clean shutdown

## Constraints

- **Never kill unrelated processes** — only kill if PID file exists AND process name matches
- **Timeout on graceful shutdown** — don't wait forever, escalate after 5 seconds
- **Best effort on forceful kill** — if it doesn't work, proceed anyway (might be permissions)

## References

- [Reliability Design](../designs/reliability.md) — shutdown existing section
- [Existing Host Flow](../flows/future/host/failure-modes/existing-host.md) — detailed scenarios
- [Socket Binding Flow](../flows/future/host/failure-modes/socket-binding.md) — bind scenarios

## Error Policy

Takeover errors should not prevent startup from attempting to continue:
1. Try graceful shutdown
2. If fails, try forceful kill
3. If fails, try socket cleanup anyway
4. If socket cleanup fails, report error and exit (can't bind)
