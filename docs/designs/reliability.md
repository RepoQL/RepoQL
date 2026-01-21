# RepoQL Reliability Design

## North Star

When something goes wrong, RepoQL tells you exactly what's wrong and either fixes it automatically or tells you how to fix it. Evidence persists. Agents don't get misled.

## Context

RepoQL uses a client-host architecture: MCP server (client) communicates with a long-lived host process via gRPC over Unix domain sockets. This introduces failure modes at multiple layers:

- **Connection**: Socket doesn't exist, host not running, channel stuck
- **Host startup**: Path validation, socket binding, database init, services
- **Runtime**: Crashes, hangs, lease expiry, resource exhaustion
- **Platform**: Windows AF_UNIX, WSL DrvFS, macOS path limits

Current state: Many failures are silent or produce confusing errors. Diagnostic information is lost when host crashes. Agents see "all OK" when the previous host crashed.

**Informed by:**
- `docs/flows/future/mcp/failure-modes/` — Client-side failure modes
- `docs/flows/future/host/failure-modes/` — Host-side failure modes
- `docs/research/failure-modes/host-startup-failure-modes.md` — 28 identified gaps

## Constraints

- Must work on Windows, macOS, Linux (including WSL)
- Must not fill user's disk (strict size limits on logs)
- Must not require user configuration for basic operation
- Must degrade gracefully rather than fail completely
- Host process may be killed at any time (crashes, OOM killer)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    MCP Server (Client)                       │
│                                                              │
│  ConnectionManager    LeaseMonitor    DiagnosticsRunner     │
│         │                  │                  │              │
│         └──────────────────┴──────────────────┘              │
│                            │                                 │
│                      RepoQlClient                            │
└────────────────────────────┼─────────────────────────────────┘
                             │ gRPC over Unix Socket
┌────────────────────────────┼─────────────────────────────────┐
│                      Host Process                            │
│                            │                                 │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐      │
│  │  Preflight  │───►│   Startup   │───►│  Services   │      │
│  │  Validator  │    │ Coordinator │    │             │      │
│  └─────────────┘    └─────────────┘    │ Embeddings  │      │
│                                        │ MCP         │      │
│  ┌─────────────┐                       │ Indexer     │      │
│  │   Health    │◄──────────────────────│ Watcher     │      │
│  │   Check     │                       └─────────────┘      │
│  └─────────────┘                                            │
│         │                                                    │
└─────────┼────────────────────────────────────────────────────┘
          │
┌─────────┼────────────────────────────────────────────────────┐
│         ▼           .repoql/                                 │
│  ┌─────────────┐                    ┌─────────────┐          │
│  │  host.log   │                    │  index.db   │          │
│  └─────────────┘                    └─────────────┘          │
└──────────────────────────────────────────────────────────────┘
```

---

## Diagnostics

When `:diagnostics:` runs or an operation fails, collect facts into `DiagnosticReport`:

```csharp
public record DiagnosticReport
{
    // Environment
    public string Cwd { get; init; }
    public string RepoRoot { get; init; }
    public string Platform { get; init; }

    // Socket
    public string SocketPath { get; init; }
    public bool SocketExists { get; init; }
    public bool SocketConnectable { get; init; }

    // Host
    public int? HostPid { get; init; }
    public bool HostRunning { get; init; }
    public string? HostProcessName { get; init; }

    // Health (from gRPC health check)
    public HealthStatus HealthStatus { get; init; }
    public string? HealthPhase { get; init; }
    public int? HealthProgress { get; init; }

    // Channel
    public ConnectivityState ChannelState { get; init; }

    // Database
    public bool DatabaseExists { get; init; }
    public bool DatabaseLocked { get; init; }
    public string? LockHolderProcess { get; init; }

    // Recent logs (last 20 lines from host.log)
    public List<string> RecentLogLines { get; init; }

    // Identified problems
    public List<string> Problems { get; init; }
}
```

**Collecting facts:**
1. Check socket exists and is connectable
2. If connectable, call gRPC health check
3. Read channel state from cached client
4. Check if database file is locked, identify lock holder
5. Read last 20 lines of `.repoql/host.log` for recent activity/errors

**Identifying problems:** Pattern match on the collected facts:

```csharp
if (!SocketExists)
    Problems.Add("Host not running (socket doesn't exist)");
else if (!SocketConnectable)
    Problems.Add("Socket exists but not connectable (stale socket?)");
else if (HealthStatus == NotServing)
    Problems.Add($"Host unhealthy: {HealthPhase}");
else if (ChannelState == TransientFailure)
    Problems.Add("gRPC channel stuck in TransientFailure");
// etc.
```

**Output:** `ToString()` renders human-readable output with facts and guidance.

---

## Host Startup

Startup happens in phases. Each phase can fail; failures are logged and surfaced.

```
Launch
  │
  ▼
Preflight ──────► Fail fast with clear error
  │ ✓
  ▼
Shutdown Existing Host (if socket exists)
  │
  ▼
Bind Socket ────► Fail: path too long, permissions, platform
  │ ✓
  ▼
Open Database ──► Fail: locked, corrupted, disk full
  │ ✓
  ▼
Start Services ─► Degrade: embeddings fail → warn, continue
  │               Degrade: watcher fail → warn, continue
  ▼
Health: SERVING
```

**Preflight checks:**
- Working directory is a git repo (or has `.repoql/`)
- Socket path length within platform limits (108 Unix, 104 macOS)
- Not running on WSL DrvFS (redirect to `/tmp` if so)
- Database path writable

**Service degradation:** Non-critical services (embeddings, file watcher, MCP integration) should warn and continue rather than crash the host. Core query functionality should work even if embeddings failed.

---

## File Persistence

Add file sink to standard .NET logging:

```csharp
var logPath = Path.Combine(repoRoot, ".repoql", "host.log");
builder.Logging.AddFile(logPath, options =>
{
    options.FileSizeLimitBytes = 1_000_000;  // 1MB max
});
```

Existing `ILogger` calls go to file. Evidence survives crashes. Register `AppDomain.UnhandledException` to log crashes before exit.

Diagnostics reads the last N lines to see what happened.

---

## Host Lifecycle

**Launch on startup** — When MCP server starts, launch host immediately. First query is fast.

**Auto-relaunch on request** — If host crashes mid-session, relaunch when next request arrives:

```
Request arrives
    │
    ▼
Socket exists and connectable?
    │
    ├─► Yes: Proceed with request
    │
    └─► No: Launch host, wait for SERVING, proceed
```

**Transparent recovery** — Request after a crash pays startup cost, but succeeds. No manual intervention.

**Circuit breaker** — If host crashes 3x in 5 minutes, stop auto-launching and surface the problem. Don't mask persistent failures.

---

## Health Check

Use standard gRPC health check (`grpc.health.v1`) fully:

**Watch + Check** — Use both:
- `Watch("")` for immediate notification when host actively changes state
- `Check("")` with timeout as periodic liveness probe (detects deadlocks/hangs)

Watch tells us when the host *can* report. A deadlocked host can't push NOT_SERVING — it's stuck. Periodic Check with short timeout (e.g., 5s) catches this.

**Per-service health** — Register health status for each degradable service:
```csharp
health.SetStatus("", ServingStatus.Serving);                    // overall
health.SetStatus("repoql.embeddings", ServingStatus.Serving);   // or NotServing if degraded
health.SetStatus("repoql.indexer", ServingStatus.Serving);
health.SetStatus("repoql.watcher", ServingStatus.Serving);
```

**Startup progress** — Standard health check is SERVING/NOT_SERVING only. For progress (45%, current file), add a separate `GetStatus` RPC.

---

## Error Messages

All errors follow the pattern:

```
❌ [What failed]
   [Observable facts]

   [Guidance]
```

Example:
```
❌ Database locked
   Path: .repoql/index.duckdb
   Lock holder: PID 12345 (DBeaver.exe)

   Close DBeaver to release the lock.
```

Never guess causes. Show what we observed.

---

## Failure Mode Coverage

| Failure Mode | Detection | Recovery |
|--------------|-----------|----------|
| Host not running | Socket doesn't exist | Auto-launch |
| Host crashed | Log ends with ERROR, host not running | Auto-relaunch, show cause from log |
| Channel stuck | ConnectivityState = TransientFailure | Dispose and reconnect |
| Database locked (external) | Lock holder identified | Guide: close the app |
| Database locked (zombie) | Lock holder is old repoql | Kill it |
| Database corrupted | DuckDB reports corruption | Delete and recreate |
| Schema incompatible | Migration fails or version mismatch | Delete and recreate |
| Wrong cwd | Suspicious path, no .git | Guide: REPOQL_CWD or primary:// |
| Socket path too long | Preflight length check | Guide: REPOQL_SOCKET |
| WSL DrvFS | Preflight filesystem check | Auto-redirect to /tmp |
| Startup hang | Health progress stalled | Show current file in log |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| File logging | Stderr only | Evidence survives crashes |
| Single 1MB log | Rolling logs | Simpler, sufficient |
| Preflight validation | Fail during init | Better error messages |
| Graceful degradation | Fail-fast services | Partial functionality > none |
| Channel state check | Assume healthy | Avoid stuck state |

## Alternatives Considered

**Watchdog process:** Rejected. Client can auto-launch; adds complexity.

**Database for diagnostics:** Rejected. Chicken-and-egg if DB is the problem.

**Automatic restart loop:** Rejected. Could mask persistent failures.

## Risks

| Risk | Mitigation |
|------|------------|
| Log fills disk | Strict 1MB limit |
| Crash not logged (SIGKILL) | Log continuously, accept limitation |
| Platform detection wrong | Conservative defaults, user overrides |
