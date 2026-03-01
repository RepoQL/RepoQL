# Local-First Recovery Design

## North Star

Restart works from any state. Diagnostics work without the host. Local state is the foundation for both — never depend on the thing you're diagnosing.

## Context

Two capabilities share the same infrastructure: reliable restart (`::host.restart`) and offline diagnostics (`::diagnostics` when the host is unreachable). Both need to read local state — socket files, PID files, host logs, database locks — without contacting the host. Both need to interpret that state into actionable information.

Today, this infrastructure exists but is fragmented:
- `TryShutdownExistingHostAsync` (host-side startup) does local-first discovery, socket probing, PID-based termination, and cleanup — but only runs when a new host is starting
- `DiagnosticsCollector` (MCP client-side) reads the same local state for diagnostic reports — but the restart command doesn't use it
- `HostRestartCommand` (MCP client-side) goes straight to a ShutdownHost RPC — fails immediately if the host is unreachable

The gap: the restart command can't handle the exact scenario where restart matters most — a crashed host.

**Enables:**
- [Reliable Restart Flow (current)](../../flows/current/mcp/host-restart.md)
- [Reliable Restart Flow (future)](../../flows/future/diagnostics/reliable-restart.md)
- [Offline Diagnostics Flow](../../flows/future/diagnostics/offline-diagnostics.md)
- [Self-Service Troubleshooting Meta-Flow](../../flows/future/diagnostics/self-service-troubleshooting.md) — stages 3-6

**Built on:**
- `DiagnosticsCollector` — existing local + remote probe infrastructure
- `DiagnosticReportProblems` — deterministic rules for problem identification
- `HostPidFile`, `HostLock`, `ProcessTermination`, `RepoQlProcessInspector` — existing process lifecycle primitives

## Constraints

- **No new persistent state** — use existing `.repoql/` artifacts (PID file, socket file, host.log, diagnostic reports). No new databases, no new services.
- **Must work cross-platform** — Windows and Unix have different socket, PID, and process semantics. Existing abstractions already handle this.
- **Must work cross-session** — when multiple MCP clients connect to the same host, restart from any client must work. PID file is the shared coordination mechanism.
- **Never kill non-RepoQL processes** — PID reuse means the old host's PID may now belong to a different process. `RepoQlProcessInspector` already handles this.
- **Best-effort probes** — individual probe failures are recorded but never stop the overall flow. DiagnosticsCollector already follows this pattern.

---

## Components

```
┌──────────────────────────────────────────────────────────┐
│                   MCP Client Process                       │
│                                                            │
│  ┌─────────────────┐    ┌─────────────────────────────┐  │
│  │ HostRestartCmd   │───▶│     DiagnosticsCollector    │  │
│  │ (::host.restart)  │    │  (local + remote probes)    │  │
│  └────────┬────────┘    └──────────────┬──────────────┘  │
│           │                             │                  │
│           │    ┌────────────────────────┘                  │
│           │    │                                           │
│           ▼    ▼                                           │
│  ┌─────────────────────────────────────────────────────┐  │
│  │              Local State (.repoql/)                    │  │
│  │                                                        │  │
│  │  HostPidFile        HostLock         Socket file      │  │
│  │  host.log           index.duckdb     diagnostics/     │  │
│  └─────────────────────────────────────────────────────┘  │
│           │                                                │
│           ▼                                                │
│  ┌─────────────────────────────────────────────────────┐  │
│  │          Process Lifecycle Primitives                   │  │
│  │                                                        │  │
│  │  ProcessTermination    RepoQlProcessInspector         │  │
│  │  DatabaseLockInspector UnixSocketTransport             │  │
│  └─────────────────────────────────────────────────────┘  │
│           │                                                │
│           ▼                                                │
│  ┌─────────────────────────────────────────────────────┐  │
│  │       DiagnosticReport + Problem Rules                 │  │
│  │                                                        │  │
│  │  DiagnosticReportProblems.Build()                     │  │
│  │  → verdict (OK / STARTING / DEGRADED / DOWN)          │  │
│  │  → problems with facts and guidance                   │  │
│  └─────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

---

## Design

### Restart as a Consumer of Diagnostics

The restart command should not contain its own local state discovery logic. `DiagnosticsCollector` already reads socket state, PID files, process state, database locks, and host logs — all best-effort, all cross-platform.

**Current restart path:**
```
ShutdownHost RPC → get PID from response → wait/kill → reconnect
```

**Designed restart path:**
```
DiagnosticsCollector.CollectAsync(Fast) → local state known
→ if socket connectable: try ShutdownHost RPC (5s deadline, optional)
→ use PID from diagnostic report (PID file or cached host state)
→ wait for exit, kill if needed
→ clean socket file, PID file
→ launch new host (clientProvider.GetClientAsync triggers launch)
→ verify via DiagnosticsCollector.CollectAsync(Fast)
```

The diagnostic report gives restart everything it needs: `SocketConnectable`, `HostProcessId`, `HostRunning`, `SocketPath`, `DbLocked`, `DbLockHolderName`. Restart interprets these facts the same way `DiagnosticReportProblems` does, then acts.

This eliminates the gap where restart fails because the host is unreachable — the local probe path always works.

### Local Cleanup Before Launch

Today, restart relies on the new host's `TryShutdownExistingHostAsync` to clean stale state. But if the new host fails to clean up (same permissions issue), startup fails and the agent is stuck — two failures from one cause.

The MCP client should clean up before launching:
1. Delete stale socket file (using `UnixSocketTransport.TryCleanupStaleSocket`)
2. Delete PID file (using `HostPidFile.TryDelete`)
3. If the database is locked by a non-RepoQL process, report it instead of launching (the new host would fail on the same lock)

`TryShutdownExistingHostAsync` already has all this logic. The MCP client cleanup mirrors it, so the new host starts into a clean environment regardless.

### Enhanced Problem Rules

`DiagnosticReportProblems.Build()` applies deterministic rules to a diagnostic report to identify problems. Seven rules exist today. The offline diagnostics flow identifies additional rules:

| New Rule | Condition | Problem Title | Guidance |
|----------|-----------|---------------|----------|
| Socket bind error | `SocketBindSucceeded == false` | "Socket bind failed" | "Check permissions on the socket directory: {SocketBindError}" |
| Host log error extraction | `HostRunning == false` + ERROR in log | (enhances "Previous host crashed") | Include the actual error line from the log, not just "inspect host log" |
| Disk space | `.repoql/` volume free space below threshold | "Low disk space" | "Free disk space on the volume containing .repoql/" |
| `.repoql/` directory missing | `RepoRoot` known but `.repoql/` doesn't exist | "No .repoql directory" | "Run a RepoQL command to initialize the repository" |
| Version mismatch (offline) | Host version file exists and differs from client | "Version mismatch" | "Client v{x}, host was v{y}. Restart may resolve." |

The socket bind error rule is the simplest — `SocketBindError` is already populated from the `socket-bind.json` artifact. The rule just checks it.

Host log error extraction replaces `host_log=error` with the actual error text. `DiagnosticReport.ToString()` already extracts error lines from the log tail for the "host log" section. The same extraction moves into the problem facts so the agent sees the crash reason in the problem, not in a separate section.

Disk space and `.repoql/` directory health are new probes in `DiagnosticsCollector`, following the existing best-effort pattern.

Version mismatch requires the host to write its version to `.repoql/host.version` on startup — a single-line file write, performed early in the startup sequence (before socket bind), so that even a crash during startup leaves a version file for offline detection. The client reads it to detect mismatches.

### Cross-Session Host State

`RepoQlClient.GetHostDiagnostics()` returns cached host process info — PID, stderr, exit code. But this only works when the current MCP client session launched the host. If a different session started the host, or the host was started via CLI, the cache is empty.

The PID file (`$repo/.repoql/host.pid`) solves the PID problem — it's written by the host immediately on startup, readable by any session.

Stderr is harder. Today, the MCP client captures stderr from the launched process into an in-memory buffer. Other sessions can't access it.

**Design:** The host writes stderr to `.repoql/host.stderr.log` in addition to the in-memory capture. Same ring-buffer approach as `host.log` — last N lines, overwritten on restart. `DiagnosticsCollector` reads it as a local probe when the in-memory cache is empty.

This is a host-side change (process wrapper writes stderr to file) with a collector-side change (new probe reads the file).

### Structured Escalation

When restart fails at a specific stage, the error message tells the agent exactly what happened and what to try next. Each failure point has a specific escalation:

| Failure | Structured error |
|---------|-----------------|
| Can't terminate process | PID, process name, kill attempted, `Manual: kill -9 {pid}` |
| Can't clean socket | Socket path, error, `Manual: rm {path}` |
| Can't bind socket | Bind error, `Check permissions on .repoql/` |
| Database locked externally | Lock holder PID and name, `Close {name} to release the lock` |
| Host didn't start (timeout) | Socket path, startup logs from stderr, `Check .repoql/host.log` |

Each escalation is a `CommandResult.Error` with enough context for both the agent and the human to act.

### Cross-Cutting Concerns

**Restart uses diagnostics, diagnostics reference restart.** The offline diagnostics report includes guidance like "Restart: `::host.restart`." The restart command uses `DiagnosticsCollector` for local state discovery. This circularity is intentional — they're the same local state, viewed from different angles.

**Error messages are the offline docs.** The bootstrapping problem — `help://` is served by the host, but recovery docs are needed when the host is down — is solved by making every `DiagnosticProblem` include actionable guidance. The guidance IS the doc. No separate lookup needed.

**Process lifecycle is already robust.** `RepoQlProcessInspector`, `ProcessTermination`, `HostPidFile`, and `HostLock` are well-tested primitives. The design reuses them, doesn't replace them.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| DiagnosticsCollector as shared probe layer | New dedicated probe component | Already built, tested, best-effort pattern established |
| PID file for cross-session coordination | Shared memory, named mutex, service registry | Simplest mechanism that works cross-platform, already implemented |
| Stderr to file for cross-session access | Shared memory, IPC, centralized log collector | Follows existing host.log pattern, readable offline |
| Inline guidance on problems | Separate help:// troubleshooting docs | More robust — doesn't depend on any infrastructure |
| Local cleanup before launch | Rely on new host's startup cleanup | Prevents cascading failure when cleanup itself fails |

## Alternatives Considered

**Dedicated `HostProbe` component:** Extract local state reading into a purpose-built probe class separate from DiagnosticsCollector. Rejected — DiagnosticsCollector already does exactly this, splitting it adds indirection without benefit. If probe logic grows complex enough to warrant separation, refactor then.

**File-based IPC for host state:** Write host health status to a JSON file periodically so clients can check state without connecting. Rejected — overcomplicates the simple case (socket connectivity test is fast and definitive) and the file would always be slightly stale.

**Supervisor process for the host:** A watchdog that monitors the host and restarts it automatically. Rejected — adds deployment complexity (which process watches the watchdog?) and removes agent control. The agent IS the supervisor.

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| PID file stale after ungraceful exit | Always verify PID against live processes via `RepoQlProcessInspector.TryGetRepoQlProcess` |
| Socket cleanup fails (permissions) | Escalate with path and error; don't launch new host into guaranteed failure |
| `DiagnosticsCollector` changes break restart | Restart depends on report fields (`SocketConnectable`, `HostProcessId`, `SocketPath`), not internal probe logic. Fields are stable. |
| Stderr log grows unbounded | Ring-buffer (last N lines), overwritten on restart, same approach as host.log |
| Multiple clients restart simultaneously | `HostLock` prevents concurrent host starts. Second restart waits for lock, discovers new host already running. |

## Extension Points

- **New problem rules** — add to `DiagnosticReportProblems.Build()` without changing the probe infrastructure
- **New local probes** — add to `DiagnosticsCollector` following the existing best-effort pattern
- **New recovery commands** — the `::host.restart` pattern (local discovery → action → verify) generalizes to any recovery command
- **Diagnostic artifacts** — the `.repoql/diagnostics/` directory is an open extension point for any component to write structured reports

## Related

- North star: `docs/north-star/diagnostics.md` (Investigation, Control, Recovery sections)
- Flow — current restart: `docs/flows/current/mcp/host-restart.md`
- Flow — future restart: `docs/flows/future/diagnostics/reliable-restart.md`
- Flow — offline diagnostics: `docs/flows/future/diagnostics/offline-diagnostics.md`
- Implementation — collector: `src/RepoQL.ConsoleApp/Diagnostics/DiagnosticsCollector.cs`
- Implementation — problem rules: `src/RepoQL.Protocol/Diagnostics/DiagnosticReport.cs`
- Implementation — restart command: `src/RepoQL.ConsoleApp/CommandImplementations/HostRestartCommand.cs`
- Implementation — host startup cleanup: `src/RepoQL.ConsoleApp/Commands/ServeCommands.cs`
- Implementation — process primitives: `src/RepoQL.ConsoleApp/Host/` (HostPidFile, ProcessTermination, RepoQlProcessInspector, HostLock)
