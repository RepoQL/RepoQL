---
description: Architecture for a development harness that keeps agents in flow while iterating on RepoQL
tags: [dev-harness, mcp, architecture, iteration]
audience: { human: 55, agent: 45 }
purpose: { design: 85, flow: 15 }
---

# Dev Harness Design

## North Star

Unbroken flow from code change to verified result. The agent thinks about the problem, not the infrastructure.

## Context

Iterating on RepoQL today requires manual choreography: deploy scripts, MCP reconnection, browser for telemetry. The harness eliminates this friction by providing a stable MCP endpoint that manages everything below it.

**Enables flows:**
- `flows/future/dev-harness/build-deploy-activate.md`
- `flows/future/dev-harness/unexpected-exit.md`
- `flows/future/dev-harness/tool-call-routing.md`
- `flows/future/dev-harness/telemetry-query.md`
- `flows/future/dev-harness/multi-session.md`

**Informed by:** `docs/north-star/dev-harness.md`

## Constraints

| Constraint | Implication |
|------------|-------------|
| Must be MCP server | Claude connects via standard protocol |
| Must work with Aspire | Leverage existing orchestrator, don't replace |
| Must survive host crashes | Harness is the stable layer |
| Must support multiple sessions | Coordination is required |
| Development only | Not for production, can make simplifying assumptions |
| Windows primary | But should work cross-platform |

---

## Components

```
┌─────────────────┐                    ┌─────────────────┐
│    Claude A     │                    │    Claude B     │
└────────┬────────┘                    └────────┬────────┘
         │ stdio MCP                            │ stdio MCP
         ▼                                      ▼
┌─────────────────┐                    ┌─────────────────┐
│   Harness A     │                    │   Harness B     │  (per-session)
│  ┌───────────┐  │                    │  ┌───────────┐  │
│  │ToolRouter │  │                    │  │ToolRouter │  │
│  │StateQuery │  │                    │  │StateQuery │  │
│  │BuildExec  │  │                    │  │BuildExec  │  │
│  │Telemetry  │  │                    │  │Telemetry  │  │
│  └───────────┘  │                    │  └───────────┘  │
└────────┬────────┘                    └────────┬────────┘
         │                                      │
         └──────────────┬───────────────────────┘
                        │ shared
                        ▼
┌───────────────────────────────────────────────────────────────┐
│                    Orchestrator (Aspire)                       │
│  - Process lifecycle (start, stop, health)                     │
│  - Shared state (who's deploying, host status)                 │
│  - Structured logs, traces, metrics                            │
└───────────────────────────┬───────────────────────────────────┘
                            │ manages
                            ▼
┌───────────────────────────────────────────────────────────────┐
│                       RepoQL Host                              │
│  - MCP server (the tools agents actually use)                  │
│  - The thing being developed                                   │
└───────────────────────────────────────────────────────────────┘
```

**Key insight:** Harness is per-session (launched via stdio like any MCP). Aspire is the shared coordination point. Multiple harness instances all query the same Aspire for state.

- Harness crashes? Claude Code restarts it (normal MCP behavior)
- Deploy RepoQL without stopping harness - harness stays up, host restarts underneath
- No special discovery - launched like any stdio MCP server

### Component Responsibilities

| Component | Responsibility | Complexity |
|-----------|---------------|------------|
| **ToolRouter** | Route calls to host or handle locally | Low - dispatch table |
| **StateManager** | Host state machine, transition logic | Medium - contained |
| **SessionTracker** | Track connections, attribute operations | Low - bookkeeping |
| **BuildExecutor** | Run dotnet build, capture output | Low - process wrapper |
| **TelemetryAgg** | Query Aspire, format for agents | Low - aggregation |
| **HostLifecycle** | Start/stop/detect via Aspire | Medium - integration |

---

## Contracts

### Harness MCP Tools

The harness exposes these tools to Claude:

```typescript
// Management tools (handled by harness, listed in MCP initialize response)
harness.build(project?: string, configuration?: string, force?: boolean): BuildResult    // atomic: stop → build → start
harness.deploy(project?: string, configuration?: string, force?: boolean): DeployResult  // atomic: stop → publish → copy → start
harness.restart(): RestartResult                                         // just restart, no build
harness.status(): StatusResult
harness.logs(filters: LogFilters): LogResult
harness.traces(filters: TraceFilters): TraceResult
harness.wait_for_operation(): WaitResult                                 // block until current build/deploy completes

// RepoQL tools (proxied to host via catch-all — any tool not starting with "harness." is forwarded)
// Not enumerated in initialize response; harness acts as transparent proxy
read(...): proxied
query(...): proxied
explore(...): proxied
import(...): proxied
// ... all other RepoQL tools, current and future
```

### BuildResult

```typescript
interface BuildResult {
  success: boolean;
  build_duration_ms: number;
  restart_duration_ms: number;
  total_duration_ms: number;
  output: string;           // stdout/stderr
  errors: CompileError[];   // structured if available
  warnings: CompileWarning[];
}
```

### DeployResult

```typescript
interface DeployResult {
  success: boolean;
  version: string;          // e.g., "1.2.3+abc1234"
  build_duration_ms: number;
  deploy_duration_ms: number;
  total_duration_ms: number;
  warnings: number;
}
```

### StatusResult

```typescript
interface StatusResult {
  host: {
    state: HostState;       // ready | starting | building | deploying | crashed
    version: string | null;
    uptime_seconds: number | null;
  };
  sessions: {
    total: number;
    this_session: string;
    operating_session: string | null;  // session running build/deploy
  };
  current_operation: Operation | null;
}
```

### Proxied Tool Response Enhancement

Successful proxied responses include harness metadata:

```typescript
interface ProxiedResponse<T> {
  // Original response from host
  ...T;

  // Harness additions
  _harness: {
    host_version: string;
    request_id: string;
    duration_ms: number;
  };
}
```

### Error Responses

```typescript
interface HarnessError {
  error: ErrorCode;
  message: string;
  // Context varies by error type
}

type ErrorCode =
  | "host_building"     // retry_after_ms included
  | "host_deploying"    // retry_after_ms included
  | "host_crashed"      // crash_id, actions included
  | "host_starting"     // will auto-resolve
  | "host_timeout"      // host didn't respond
  | "build_in_progress" // conflict info included
  | "deploy_in_progress" // conflict info included
  | "build_failed"      // errors included
  | "deploy_failed"     // errors included
  | "aspire_unavailable" // Aspire MCP server unreachable after retries
  | "crash_not_found"   // crash_id doesn't exist
  | "request_not_found"; // request_id not in recent log
```

---

## State Management

### Host States

```
┌──────────┐
│ starting │◄───────────────────────────────────────┐
└────┬─────┘                                        │
     │ health OK                                    │
     ▼                                              │
┌──────────┐  build()    ┌──────────┐               │
│  ready   │────────────►│ building │───────────────┤
└────┬─────┘             └────┬─────┘               │
     │                        │ unexpected exit     │
     │  deploy()              ▼                     │
     │               ┌──────────┐                   │
     └──────────────►│ deploying│───────────────────┘
                     └────┬─────┘
     │                    │
     │ unexpected exit    │ unexpected exit
     ▼                    ▼
┌──────────┐◄─────────────┘
│ crashed  │
└────┬─────┘
     │ restart() or build() or deploy()
     │
     └─────────────────────────────────────────────►starting/building/deploying
```

### State Transitions

| From | Event | To | Action |
|------|-------|-----|--------|
| `starting` | Health OK | `ready` | Enable routing |
| `starting` | Exit | `crashed` | Build crash report |
| `ready` | `build()` | `building` | Suspend routing, stop host, build |
| `ready` | `deploy()` | `deploying` | Suspend routing, stop host, publish |
| `ready` | Unexpected exit | `crashed` | Build crash report |
| `building` | Build succeeds, host healthy | `ready` | Activate routing |
| `building` | Build/startup fails | `crashed` | Restart old version attempted, then crash report |
| `deploying` | Deploy succeeds, host healthy | `ready` | Activate routing |
| `deploying` | Deploy/startup fails | `crashed` | Restart old version attempted, then crash report |
| `crashed` | `restart()` | `starting` | Launch host |
| `crashed` | `build()` | `building` | Stop host, build |
| `crashed` | `deploy()` | `deploying` | Stop host, publish |

### Exit Classification

The harness maintains a set of "expected exits" - shutdowns it initiated:

```csharp
interface IExitClassifier
{
    void ExpectExit(string reason);  // Called before shutdown
    bool WasExpected();              // Called when exit detected
    void Clear();                    // Called after handling
}
```

Any exit not in this set triggers the crash flow.

---

## Session Management

### Per-Session Harness

Each harness instance IS a session. No explicit session tracking needed within a harness - it only serves one Claude connection.

```csharp
record HarnessIdentity(
    string SessionId,      // Generated on startup
    int ProcessId,
    DateTime StartedAt
);
```

### Shared State via Aspire

Coordination happens through Aspire (or a shared state file):

```csharp
interface ISharedState
{
    Task<CurrentOperation?> GetCurrentOperationAsync();
    Task RegisterOperationAsync(string sessionId, OperationType type);
    Task CompleteOperationAsync(string sessionId);
}

enum OperationType { Build, Deploy }

record CurrentOperation(
    string SessionId,
    OperationType Type,
    DateTime StartedAt
);
```

Both build and deploy operations are serialized across sessions.

### Conflict Resolution

When Harness B calls `deploy()` while Harness A is deploying:

1. Harness B queries shared state via `GetCurrentDeploy()`
2. Sees Session A is deploying
3. Returns `deploy_in_progress` error with Session A's info
4. Session B can:
   - `wait_for_operation()` - poll until A completes
   - `deploy({ force: true })` - queue after A
   - Do something else

**Design decision:** No implicit queueing. Agent explicitly chooses to wait or force.

---

## Aspire Integration

The harness is an **MCP client to the Aspire dashboard's MCP server**, which exposes lifecycle control and telemetry via HTTP streaming.

```
Harness ──MCP/HTTP──► Aspire Dashboard MCP Server (http://localhost:18891)
                              │
                              ├─ execute_resource_command (start/stop/custom)
                              ├─ list_resources (state, health, commands)
                              ├─ list_structured_logs
                              ├─ list_traces
                              └─ list_console_logs
```

### Lifecycle Control

The harness controls host lifecycle through Aspire's MCP server:

```csharp
interface IHostLifecycle
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<HealthStatus> CheckHealthAsync(CancellationToken ct);
    Task<ResourceInfo> GetStatusAsync(CancellationToken ct);
}
```

Implementation uses Aspire MCP tools:
- `execute_resource_command("host", "start")` / `"stop"` / `"restart"`
- `execute_resource_command("host", "rebuild_and_restart")` - custom command already defined in orchestrator
- `list_resources` - state, health, endpoints, available commands

### Telemetry Access

```csharp
interface ITelemetryAggregator
{
    Task<LogResult> QueryLogsAsync(LogFilters filters);
    Task<TraceResult> QueryTracesAsync(TraceFilters filters);
    Task<LogResult> GetCrashContextAsync(string crashId);
}
```

Implementation uses Aspire MCP tools:
- `list_structured_logs` - structured logs with filtering
- `list_traces` - distributed traces
- `list_trace_structured_logs` - logs for specific trace
- `list_console_logs` - raw console output per resource

### Connection Resilience

The Aspire dashboard may restart (user action, crash, update). The harness must handle this transparently.

**Detection:** MCP calls fail with connection error or "session not found" response.

**Recovery strategy:**

```csharp
interface IAspireConnection
{
    Task<T> CallAsync<T>(Func<IMcpClient, Task<T>> operation, CancellationToken ct);
}

// Implementation with automatic reconnection
class ResilientAspireConnection : IAspireConnection
{
    private IMcpClient? _client;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    public async Task<T> CallAsync<T>(Func<IMcpClient, Task<T>> operation, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var client = await GetOrCreateClientAsync(ct);
                return await operation(client);
            }
            catch (Exception ex) when (IsRecoverableError(ex))
            {
                _client = null;  // Force reconnect on next attempt
                if (attempt == MaxRetries - 1) throw;
                await Task.Delay(RetryDelay, ct);
            }
        }
        throw new UnreachableException();
    }

    private bool IsRecoverableError(Exception ex) =>
        ex is HttpRequestException ||
        ex is McpSessionNotFoundException ||
        ex.Message.Contains("session not found", StringComparison.OrdinalIgnoreCase);
}
```

**Behavior on Aspire restart:**

| Scenario | Harness behavior |
|----------|------------------|
| Aspire restarts while idle | Next call reconnects transparently |
| Aspire restarts mid-operation | Operation fails, retry reconnects, operation retried |
| Aspire down for extended period | Calls fail with clear error after retries exhausted |
| Aspire endpoint changes | Harness reads endpoint from well-known location on reconnect |

**State after reconnection:**

- Host state is queried fresh from Aspire (it's the source of truth)
- In-flight operations that failed are **not** automatically retried at the operation level (build/deploy)
- The harness reports the failure; agent decides whether to retry

**Configuration:**

```csharp
record AspireConnectionOptions
{
    string EndpointUrl { get; init; } = "http://localhost:18891";
    TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    int MaxRetries { get; init; } = 3;
    TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}

---

## Build Execution

### Build Pipeline

```csharp
interface IBuildExecutor
{
    Task<BuildResult> BuildAsync(BuildOptions options, IProgress<string> output);
    Task<DeployResult> PublishAsync(PublishOptions options, IProgress<string> output);
}

record BuildOptions(
    string Project = "RepoQL.ConsoleApp",
    string Configuration = "Debug",
    bool Clean = false
);

record PublishOptions(
    string Project = "RepoQL.ConsoleApp",
    string Configuration = "Debug",
    string OutputPath = "./publish"
);
```

### build() Flow

1. Stop current host (via Aspire) - **required due to file locks**
2. Run `dotnet build`
3. Stream stdout/stderr to progress callback
4. Parse MSBuild output for structured errors/warnings
5. If build fails: restart old host, return errors
6. If build succeeds: start host (via Aspire)

No artifact copy - the host runs from the build output directory.

### deploy() Flow

1. Stop current host (via Aspire)
2. Run `dotnet publish -o ./publish`
3. Copy published artifacts to deployment location
4. If publish/copy fails: restart old host, return errors
5. If publish succeeds: start host from deployment location (via Aspire)

**Design decision:** `deploy()` uses file copy to deployment location. `build()` runs from build output directly. Both require stopping host first due to Windows file locks.

---

## Cross-Cutting Concerns

### Request Correlation

Every proxied tool call gets a request ID:

```
Claude → Harness: query({ sql: "..." })
         │
         ├─ Harness generates: req_abc123
         │
         ├─ Forwards to host (request ID tracked harness-side only)
         │
         ├─ Response includes req_abc123 in _harness metadata
         │
         └─ Agent can use req_abc123 to query logs/traces from that time window
```

This enables: "Show me the trace for req_abc123" — harness uses the timestamp from the request to filter telemetry.

### Error Consistency

All errors follow the same structure:
- `error`: Machine-readable code
- `message`: Human-readable description
- Context fields vary by error type

Agents can pattern-match on `error` code, humans can read `message`.

### Logging

The harness logs to:
1. Its own stdout (for human observation)
2. Aspire (if connected) for aggregated telemetry

Log levels:
- `debug`: State transitions, routing decisions
- `info`: Operations (build, deploy, restart)
- `warn`: Recoverable issues (timeout, retry)
- `error`: Failures (build error, crash)

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Per-session harness | Singleton harness | Standard MCP lifecycle, no discovery problem |
| Shared state via Aspire | Direct harness-to-harness | Aspire already exists, provides telemetry |
| Fail fast during deploy | Queue tool calls | Simpler, agent can decide to wait |
| No auto-recovery on crash | Retry with backoff | Crashes are bugs, surface them |
| Serialize deploys | Concurrent deploys | Avoid race conditions, acceptable latency |
| Stop before build | Build while running | File locks on Windows prevent in-place build |
| Aspire for lifecycle | Direct process management | Already invested, provides telemetry |

## Alternatives Considered

**Singleton harness (rejected)**
One persistent harness process that all sessions connect to. Rejected because:
- Requires special discovery mechanism (how does Claude find it?)
- Doesn't match standard MCP lifecycle (stdio launch)
- Single point of failure for all sessions

**File-based coordination without Aspire (considered)**
Lock file for deploy coordination. Could work, but Aspire already provides shared state and we're using it anyway for lifecycle.

**Auto-restart on crash (rejected)**
Would hide bugs. The north star explicitly calls this out: unexpected exits are information, not things to paper over.

**Queue tool calls during deploy (rejected)**
Adds complexity (timeouts, ordering, cancellation). Fail-fast with retry guidance is simpler and gives agent control.

**Extend Aspire directly (deferred)**
Make harness functionality part of Aspire. Possible, but keeping separate allows faster iteration and clearer boundaries. Could consolidate later.

**WebSocket push for notifications (deferred)**
Would enable push notifications (crash alerts, deploy complete). MCP doesn't support this natively. Could add later; for now, errors surface on next tool call.

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Harness itself crashes | Low | High | Minimal surface area, no complex logic in critical path |
| Aspire connection instability | Medium | Medium | `ResilientAspireConnection` with auto-reconnect (see Connection Resilience) |
| Build times exceed expectations | Medium | Low | Progress streaming, agent can observe |
| State machine bugs | Medium | Medium | Extensive logging, explicit transitions |
| Session tracking overhead | Low | Low | Simple data structures, no persistence |

## Extension Points

| Extension | Mechanism |
|-----------|-----------|
| Additional tools | Add to harness tool registry |
| Custom build commands | `BuildOptions` extensible |
| Different orchestrators | `IHostLifecycle` interface |
| Push notifications | Future MCP extension or sideband |
| Persistent crash history | Add to `ITelemetryAggregator` |

---

## Verification

**The test:** Could a skilled developer look at this and say "yep, that will work" before writing code?

Checklist:
- [x] Flows this enables are documented
- [x] Cross-cutting concerns resolved (correlation, errors, logging)
- [x] Complexity contained (state machine, session tracking isolated)
- [x] Technologies chosen with rationale (MCP, Aspire, file copy)
- [x] Trade-offs explicit
- [x] Alternatives recorded
- [x] Failure modes addressed (crash flow, build failure, conflicts)
- [x] Extension points identified

## Related

- North star: `docs/north-star/dev-harness.md`
- Flows: `docs/flows/future/dev-harness/`
