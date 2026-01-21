# Plan: Host Persistence

Implements: [Reliability Design](../designs/reliability.md) — File Persistence section

## Scope

**Covers:**
- File sink configuration for host logging
- Crash-safe logging (flush after writes)

**Does not cover:**
- Reading logs in diagnostics (Plan: Diagnostics)
- Preflight validation (Plan: Preflight Validation)
- Client-side connection handling (Plan: Connection Recovery)

## Enables

Once Host Persistence exists:
- **Evidence survives crashes** — logs persist even when host dies
- **Plan: Diagnostics** can show recent log lines from `host.log`
- **Agents see what went wrong** — last log lines show the crash

## Prerequisites

- Host uses `Microsoft.Extensions.Logging` (already true)
- `.repoql/` directory created during startup (already true)

## North Star

When the host crashes and restarts, the log file shows exactly what happened — the last lines are the evidence.

## Done Criteria

### File Logging

- The host shall configure a file sink writing to `{repoRoot}/.repoql/host.log`
- The file sink shall limit file size to 1MB
- The file sink shall flush after each write (crash safety)
- When file exceeds limit, the sink shall truncate or rotate per provider behavior

### Startup Logging

- The host shall log "Host starting" with PID and version at startup
- The host shall log phase transitions (preflight, socket bind, database init, services, ready)
- The host shall log "Host ready" when entering SERVING state

### Shutdown Logging

- The host shall log "Host shutting down" on clean shutdown
- When unhandled exception occurs, the host shall log the exception before exit
- The host shall register `AppDomain.UnhandledException` to log crashes

### Expected Disconnection Handling

- When lease stream is cancelled by client disconnect, log at DEBUG not ERROR
- Client disconnection is normal (MCP server closed, user switched repos)
- Do not log scary stack traces for expected cancellations
- Log "Client disconnected" at INFO level, not "RpcException: Cancelled"

## Constraints

- **1MB max log size** — must not fill user's disk
- **Standard logging** — use existing `ILogger` infrastructure, no custom logger
- **Flush after critical events** — startup, phase changes, errors, shutdown

## References

- [Reliability Design](../designs/reliability.md) — logging decision
- [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) — if using Serilog
- [Microsoft.Extensions.Logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging) — built-in logging

## Error Policy

Logging should never prevent shutdown:
1. Log the event
2. If logging fails, continue anyway
3. Crash evidence is best-effort
