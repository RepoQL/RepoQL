---
description: Plan for reliable restart — restart consuming DiagnosticsCollector, local cleanup, structured escalation
tags: [diagnostics, restart, recovery, local-first, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Reliable Restart

Implements: [Local-First Recovery Design](../../../designs/future/local-first-recovery.md) — Restart as Consumer of Diagnostics, Local Cleanup Before Launch, Structured Escalation

## Scope

**Covers:**
- Refactor `HostRestartCommand` to use `DiagnosticsCollector.CollectAsync(Fast)` for local state discovery
- Local cleanup before launch: delete stale socket, delete PID file, check DB lock
- Graceful shutdown attempt via ShutdownHost RPC when socket is connectable (5s deadline, optional)
- PID-based process termination using diagnostic report data
- Post-launch verification via `DiagnosticsCollector.CollectAsync(Fast)`
- Structured escalation: each failure point returns a `CommandResult.Error` with specific context and recovery guidance
- Tests for the new restart flow

**Does not cover:**
- Enhanced problem rules (Plan: [01-problem-rules](01-problem-rules.md))
- Cross-session host state (Plan: [02-cross-session-state](02-cross-session-state.md))
- Queue commands (Plan: [06-queue-commands](06-queue-commands.md))

## Enables

- `::host.restart` works when the host has crashed — the single most important gap today
- Restart works from any state: host running, host crashed, socket stale, host partially started, previous restart in progress
- Cascading failures prevented — local cleanup before launch means the new host starts into a clean environment
- North-star satisfied: "An agent should be able to reliably restart a dead host to a known-good state"

## Prerequisites

- Plan: [01-problem-rules](01-problem-rules.md) — richer diagnostic reports make restart decisions better-informed
- Plan: [02-cross-session-state](02-cross-session-state.md) — cross-session PID and stderr visibility helps restart find the right process

Both are soft prerequisites — restart can be built without them, but the diagnostic report it consumes will be richer if they're complete first.

## North Star

Restart works the first time, from any state, without the agent needing to understand why the host is down. The agent runs `::host.restart` and either the host comes back (common case) or the agent gets a specific error with a specific next step (rare case). No blind retries.

## Done Criteria

### Local State Discovery

- `HostRestartCommand` shall call `DiagnosticsCollector.CollectAsync` with `DiagnosticCollectionMode.Fast` as its first action
- The command shall extract from the diagnostic report: `SocketConnectable`, `HostProcessId`, `SocketPath`, `DbLocked`, `DbLockHolderName`, `HostRunning`
- The command shall not call `clientProvider.GetClientAsync()` before local state is known — that call currently fails when the host is unreachable and is the root cause of the restart gap

### Graceful Shutdown Attempt

- When `SocketConnectable == true`, the command shall attempt `ShutdownHost` RPC with a 5-second deadline
- When the RPC succeeds, proceed to wait for process exit
- When the RPC fails (timeout, unavailable), proceed to PID-based termination — the RPC is optional, not required
- When `SocketConnectable == false`, skip the RPC entirely

### Process Termination

- The command shall use `HostProcessId` from the diagnostic report (sourced from PID file or cached host state)
- When `HostProcessId` is available, verify it's still a RepoQL process via `RepoQlProcessInspector.TryGetRepoQlProcess` before termination
- When the process is confirmed as RepoQL, wait up to 10 seconds for graceful exit, then terminate forcefully via `ProcessTermination`
- When `HostProcessId` is null or the PID belongs to a non-RepoQL process, skip termination — proceed to cleanup
- The command shall never kill a non-RepoQL process — design constraint

### Local Cleanup

- Before launching a new host, the command shall:
  1. Delete stale socket file via `UnixSocketTransport.TryCleanupStaleSocket` (or equivalent on the current platform)
  2. Delete PID file via `HostPidFile.TryDelete`
  3. Check database lock state from the diagnostic report
- If `DbLocked == true` and `DbLockHolderName` is not a RepoQL process, return an error instead of launching — the new host would fail on the same lock
- Each cleanup step shall be best-effort: if deletion fails, record the failure and continue to the next step

### Launch and Verification

- After cleanup, the command shall trigger host launch via `clientProvider.GetClientAsync()` (existing mechanism)
- After launch, the command shall run `DiagnosticsCollector.CollectAsync(DiagnosticCollectionMode.Fast)` to verify the new host is running
- When verification shows `SocketConnectable == true` and verdict is `OK` or `STARTING`, return success
- When verification fails (timeout, host didn't start), return a structured error with startup context

### Structured Escalation

- Each failure point shall return a `CommandResult.Error` with:
  - What failed (specific stage)
  - Relevant facts from the diagnostic report
  - A specific recovery suggestion the agent can act on
- The following escalation table shall be implemented:

| Failure | Error includes |
|---------|---------------|
| Process termination failed | PID, process name, whether kill was attempted, `Manual: kill -9 {pid}` |
| Socket cleanup failed | Socket path, error message, `Manual: rm {path}` |
| Socket bind failed (post-launch) | Bind error from verification diagnostic report, `Check permissions on .repoql/` |
| Database locked externally | Lock holder PID and name, `Close {name} to release the lock` |
| Host didn't start (verification timeout) | Socket path, last stderr lines if available, `Check .repoql/host.log` |

- A test shall verify that each failure path produces the correct structured error
- A test shall verify the happy path: host running → graceful shutdown → cleanup → relaunch → verify

## Constraints

- **Never kill non-RepoQL processes** — PID reuse means the old host's PID may now belong to a different process. Always verify via `RepoQlProcessInspector` before termination. Design constraint.
- **HostLock prevents concurrent starts** — if two clients restart simultaneously, the second waits for the lock and discovers the new host is already running. Existing mechanism, no changes needed.
- **5-second RPC deadline** — long enough for graceful shutdown, short enough that a hanging host doesn't block restart. The RPC is optional — restart must succeed even when it times out.
- **DiagnosticsCollector is the only probe path** — the restart command shall not read PID files, socket files, or host logs directly. All local state comes through the diagnostic report. Design decision: "The restart command should not contain its own local state discovery logic."

## References

- [Local-First Recovery Design](../../../designs/future/local-first-recovery.md) — Restart as Consumer of Diagnostics, Local Cleanup, Structured Escalation sections
- [Reliable Restart Flow (future)](../../../flows/future/diagnostics/reliable-restart.md) — full stage-by-stage flow
- [Self-Service Troubleshooting Meta-Flow](../../../flows/future/diagnostics/self-service-troubleshooting.md) — stages 5-6 use restart
- `src/RepoQL.ConsoleApp/CommandImplementations/HostRestartCommand.cs` — current implementation to refactor
- `src/RepoQL.ConsoleApp/Diagnostics/DiagnosticsCollector.cs` — probe infrastructure
- `src/RepoQL.ConsoleApp/Commands/ServeCommands.cs` — `TryShutdownExistingHostAsync` contains the cleanup logic to mirror
- `src/RepoQL.ConsoleApp/Host/` — `HostPidFile`, `ProcessTermination`, `RepoQlProcessInspector`, `HostLock`
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions

## Error Policy

Restart is a recovery command — it must be maximally resilient. Each stage operates best-effort:
1. Graceful shutdown fails → proceed to kill
2. Kill fails → proceed to cleanup
3. Cleanup fails → proceed to launch anyway (the new host's `TryShutdownExistingHostAsync` is a second chance)
4. Launch fails → return structured error with everything known

Only one condition halts restart early: database locked by a non-RepoQL process. The new host would fail on the same lock, so launching would waste time and produce a confusing error.
