---
description: Comprehensive analysis of failure modes in RepoQL client-server communication
tags: [reliability, diagnostics, failure-modes, client-server]
audience: { human: 40, agent: 60 }
purpose: { research: 80, reference: 20 }
---

# Client-Server Failure Modes

This document catalogs all known failure modes in the RepoQL client-server architecture, their symptoms, detection methods, and recovery paths.

## Architecture Overview

```
┌─────────────────┐         ┌─────────────────┐
│   MCP Server    │         │      Host       │
│   (Client)      │         │  (repoql serve) │
├─────────────────┤  gRPC   ├─────────────────┤
│ GrpcChannel     │◄───────►│ RepoQlService   │
│ Lease Stream    │  Unix   │ LeaseRegistry   │
│ Heartbeat Loop  │  Socket │ DuckDB          │
└─────────────────┘         └─────────────────┘
```

**Key components:**
- **GrpcChannel**: Pooled HTTP/2 connections over Unix socket
- **Lease Stream**: Client-streaming RPC that keeps host alive
- **Heartbeat Loop**: Sends beats every 10s on lease stream
- **LeaseRegistry**: Host-side tracking of active clients
- **IdleShutdown**: Shuts down host after 45s with no leases

---

## Host-Side Failures

### H1: Host Never Started

**Cause**: No prior connection attempt, or launch failed silently.

**Symptoms**:
- Socket file does not exist
- Connection refused

**Detection**: Check `File.Exists(socketPath)`

**Recovery**: Tools auto-launch host on first connection.

**Code path**: `RepoQlClient.EnsureServerRunning()` → `LaunchHost()`

---

### H2: Host Crashed

**Cause**: Unhandled exception, OOM, segfault.

**Symptoms**:
- Socket file exists but nothing listening
- Connection refused
- Host process exited (check PID)

**Detection**:
```
socket file exists: yes
connect: refused
host PID: exited (code 1)
```

**Recovery**: Next connection attempt will launch new host.

**Evidence**: `RepoQlClient.GetHostDiagnostics()` captures stderr, exit code.

**Code path**: `RepoQlClient.TryHealthCheckAsync()` returns false → `LaunchHost()`

---

### H3: Host Hanging on Startup

**Cause**: Slow DuckDB initialization, large repo scan, resource contention.

**Symptoms**:
- Socket file may or may not exist
- Connect timeout or connect succeeds but health check hangs
- Host process running but not responding

**Detection**:
```
socket file: yes (age: 45s)
connect: timeout (2s)
host PID: running
```

**Recovery**: Wait longer, or kill and restart.

**Timeout**: 120s default (`REPOQL_START_TIMEOUT_MS`)

**Code path**: `RepoQlClient.EnsureServerRunning()` polls health check in loop.

---

### H4: Host Unhealthy

**Cause**: Internal error during initialization, missing dependencies.

**Symptoms**:
- Connect succeeds
- gRPC health check returns NOT_SERVING or fails

**Detection**:
```
connect: ok
health: NOT_SERVING
```

**Recovery**: Check host stderr for cause, restart.

**Code path**: `RepoQlClient.TryHealthCheckAsync()` → `HealthServing()`

---

### H5: Host OOM During Query

**Cause**: Large result set, memory-intensive operation.

**Symptoms**:
- Query hangs, then fails with RpcException
- Host may crash (H2) or survive

**Detection**: Host stderr shows `OutOfMemoryException`

**Recovery**:
- Reduce query scope
- Set `REPOQL_EMBED_BATCH_SIZE` lower
- Increase host memory

**Code path**: `RepoQlServiceImpl.ExecuteRawQuery()` → `_db.Query()`

---

### H6: Database Locked

**Cause**: Another process has DuckDB file open.

**Symptoms**:
- Health check fails or queries fail
- Error: "Could not set lock on file"

**Detection**: Error message contains "lock" and "Resource busy"

**Recovery**:
- Find and kill other process: `ps aux | grep repoql`
- Run `repoql serve` (shuts down existing)

**Code path**: `DuckDbDataStore` constructor fails to open DB.

---

### H7: Database Corrupt

**Cause**: Crash during write, disk error, concurrent access violation.

**Symptoms**:
- Queries fail with DuckDB internal errors
- Inconsistent results

**Detection**: DuckDB error messages about corruption, invalid pages.

**Recovery**: Delete `.repoql/index.duckdb` and reindex.

**Code path**: Various query failures in `DuckDbDataStore`.

---

### H8: Socket Path Too Long

**Cause**: Repository in deep directory structure.

**Symptoms**:
- Host can't create socket
- Path length ≥108 characters

**Detection**: Check path length against Unix socket limit (108).

**Recovery**: Set `REPOQL_SOCKET=/tmp/repoql-project.sock`

**Code path**: `RepoDirectoryAccessor.ResolveSocketPath()`

---

### H9: Launch Race Condition

**Cause**: Two clients simultaneously detect "no host" and both launch.

**Symptoms**:
- Intermittent startup failures
- First host killed by second
- Brief period where neither is ready

**Detection**: Multiple `repoql serve` processes briefly visible.

**Current mitigation**: Second `serve` sends ShutdownHost RPC to first.

**Gap**: No file lock prevents the race. Both clients may experience failures during the window.

**Code path**: `ServeCommands.TryShutdownExistingHostAsync()` handles existing host.

---

### H10: Host Can't Bind Socket

**Cause**: Permissions, path doesn't exist, socket file locked.

**Symptoms**:
- Host exits immediately
- Error in stderr about socket binding

**Detection**: Host stderr shows bind error.

**Recovery**: Check directory permissions, clear stale socket file.

---

## Client-Side Failures

### C1: Channel Stuck in TransientFailure

**Cause**: Connection failed and channel entered failed state without recovering.

**Symptoms**:
- **Instant timeout on all calls**
- Fresh connection probe succeeds (diagnostics say "connected")
- Host is healthy
- Only fix is restart MCP server

**Detection gap**: Current diagnostics create fresh socket, don't test cached channel.

**Missing probe**: `_channel.State` — should check for `TransientFailure` or `Shutdown`.

**Code path**: `GrpcChannel` internal state machine. `InvokeWithReconnectAsync()` disposes channel on certain errors but not all.

**This is the scenario described by the user.**

---

### C2: Channel Disposed But Referenced

**Cause**: Race between dispose and usage, or bug in cleanup.

**Symptoms**:
- `ObjectDisposedException` on calls
- Should trigger reconnect (in `ShouldAttemptReconnect`)

**Detection**: Exception type.

**Recovery**: Automatic reconnect attempt.

**Code path**: `DisposeChannel()` sets `_channel = null`, but caller may have captured reference.

---

### C3: Lease Stream Closed Silently

**Cause**: Host closed the stream, network interruption.

**Symptoms**:
- Client thinks it's connected
- Host has evicted the lease (no heartbeat for 30s)
- Next query may work (reconnect) or fail

**Detection gap**: `_leaseCall` never checked for faulted state.

**Missing probe**: Check if lease stream is still open.

**Code path**: `EstablishLeaseOrThrow()` stores `_leaseCall` but never monitors it.

---

### C4: Heartbeat Failures Swallowed

**Cause**: Network error during heartbeat write.

**Symptoms**:
- Client unaware that heartbeats aren't reaching host
- Host evicts lease after 30s
- Client discovers problem on next query

**Root cause in code** (`RepoQlClient.cs` lines 629-645):
```csharp
_ = Task.Run(async () =>
{
    while (!_leaseCts.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), _leaseCts.Token);
        await leaseCall.RequestStream.WriteAsync(new ClientLeaseBeat {...});
        // NO TRY-CATCH — exceptions swallowed by Task.Run
    }
}, _leaseCts.Token);
```

**Gap**: Fire-and-forget with no error handling. Client has no idea if heartbeats are failing.

**Impact**: Host evicts lease, idle shutdown may kill host, client confused.

---

### C5: Requests Stuck In Flight

**Cause**: Host hanging, network stall, gRPC deadlock.

**Symptoms**:
- Calls hang indefinitely (or until deadline)
- Channel may appear healthy

**Detection gap**: No tracking of in-flight requests or their age.

**Missing probe**: Count of pending requests, age of oldest.

**Code path**: `InvokeWithReconnectAsync()` awaits call with deadline.

---

### C6: Reconnect Loop Exhausted

**Cause**: Persistent failure, wrong configuration.

**Symptoms**:
- Two attempts made, both failed
- Exception thrown to caller

**Detection**: Exception after retry.

**Gap**: No circuit breaker. Repeated failures keep trying 2x each time.

**Code path**: `InvokeWithReconnectAsync()` has `maxAttempts = 2`.

---

### C7: Connection Pool Exhaustion

**Cause**: Many concurrent requests, slow responses, connection leaks.

**Symptoms**:
- New requests queue waiting for connection
- Timeouts increase
- Eventual failures

**Configuration** (`RepoQlClient.cs` line 153):
```csharp
MaxConnectionsPerServer = 10
```

**Detection gap**: No visibility into pool state.

**Code path**: `SocketsHttpHandler` manages pool internally.

---

## Race Conditions

### R1: Host Shutdown During Connect

**Cause**: Host idle shutdown triggers while client connecting.

**Symptoms**:
- Connect succeeds, then health check fails
- Or connect fails mid-handshake

**Detection**: Connect ok → immediate health failure.

**Recovery**: Retry triggers new host launch.

**Code path**: `IdleShutdownHostedService.ExecuteAsync()` calls `lifetime.StopApplication()`.

---

### R2: Lease Evicted During Request

**Cause**: Request takes longer than lease TTL without heartbeat reaching host.

**Symptoms**:
- Request may succeed
- But lease is gone
- Next request sees stale state

**Timing**:
- Heartbeat interval: 10s
- Lease TTL: 30s
- Grace period: 45s

**Code path**: `IdleShutdownHostedService` evicts stale leases every 5s.

---

## Detection Matrix

| Failure | Socket | Connect | Health | Host PID | DB Lock | Channel | Lease |
|---------|--------|---------|--------|----------|---------|---------|-------|
| H1: Never started | ✗ | - | - | - | unlocked | - | - |
| H2: Crashed | ✓ | refused | - | exited | unlocked | - | - |
| H3: Hanging | ✓/✗ | timeout | - | running | locked | - | - |
| H4: Unhealthy | ✓ | ✓ | ✗ | running | locked | - | - |
| H5: OOM | ✓ | ✓ | ✓ | may exit | ? | - | - |
| H6: DB Locked | ✓ | ✓ | ✗ | running | **other PID** | - | - |
| Zombie | ✓ | refused | - | none | **locked** | - | - |
| C1: Channel stuck | ✓ | ✓ (fresh) | ✓ (fresh) | running | locked | **TransientFailure** | - |
| C3: Lease closed | ✓ | ✓ | ✓ | running | locked | Ready | **closed** |
| C4: Heartbeat fail | ✓ | ✓ | ✓ | running | locked | Ready | open |

---

## Current Detection Capabilities

| Probe | Implemented | Location |
|-------|-------------|----------|
| Socket file exists | ✓ | `SelfTestRunner.CheckSocketPath()` |
| Socket connect (fresh) | ✓ | `SelfTestRunner.CheckConnectionAsync()` |
| gRPC health (fresh) | ✓ | `SelfTestRunner.CheckHealthAsync()` |
| Host PID and exit code | ✓ | `RepoQlClient.GetHostDiagnostics()` |
| Host stderr capture | ✓ | `RepoQlClient.StartProcess()` |
| Indexer status | ✓ | `indexing_diagnostics()` UDF |
| **Database file lock** | ✗ | Missing |
| **Cached channel state** | ✗ | Missing |
| **Lease stream state** | ✗ | Missing |
| **In-flight request count** | ✗ | Missing |
| **Heartbeat success/fail** | ✗ | Missing |
| **Connection pool state** | ✗ | Missing |

---

## Database File Lock Diagnostics

The DuckDB database file (`.repoql/index.duckdb`) can be locked by:
- The running host process (expected)
- A zombie host process that didn't clean up
- Another tool that opened the file directly
- A previous host that's still shutting down

### Probe: Who Has the Lock?

**On Linux/macOS:**
```bash
lsof .repoql/index.duckdb
fuser .repoql/index.duckdb
```

**On Windows:**
```powershell
# Requires handle.exe from Sysinternals, or:
Get-Process | Where-Object { $_.Modules.FileName -like "*index.duckdb*" }
```

### What Lock State Tells Us

| Lock State | Host PID | Meaning |
|------------|----------|---------|
| Locked | matches known host | Normal operation |
| Locked | different PID | Zombie or competing process |
| Locked | no host running | Zombie didn't release lock |
| Unlocked | host running | Host hasn't opened DB yet (starting) |
| Unlocked | no host | Ready for new host |

### Detection Value

1. **H6 diagnosis**: "DB locked by PID 12345, not our host (PID 67890)"
2. **Zombie detection**: "DB locked by PID 12345 but no host process found"
3. **Startup sequencing**: "Host running but DB not yet locked (still initializing)"
4. **Stale state**: "Socket exists, no host, but DB locked — zombie cleanup needed"

### Implementation Approach

```csharp
// Cross-platform lock check
public static LockInfo CheckDatabaseLock(string dbPath)
{
    // Try to open with exclusive access
    try
    {
        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return LockInfo.Unlocked;
    }
    catch (IOException)
    {
        // Locked - try to find who has it
        return LockInfo.Locked(FindLockingProcess(dbPath));
    }
}
```

For finding the locking process:
- **Windows**: P/Invoke to `NtQuerySystemInformation` or shell out to `handle.exe`
- **Linux**: Parse `/proc/locks` or use `lsof`
- **macOS**: Use `lsof`

---

## Critical Gap: Fresh vs Cached Probes

The most impactful gap is that diagnostics test a **fresh connection** while actual requests use a **cached channel**.

```
Diagnostic probe:  new Socket() → connect → health → "OK"
Actual request:    _channel → call → hangs/fails instantly
```

**Scenario (user-reported)**:
1. Channel enters TransientFailure state
2. Calls fail instantly with timeout
3. `:diagnostics:` creates fresh connection, reports "connected"
4. User confused: diagnostics say OK but calls fail
5. Only fix: restart MCP server

**Required**: Diagnostic must probe the actual cached channel, not create a fresh one.

---

## Recommendations

### High Priority

1. **Probe cached channel state**: Add `_channel?.State` check to diagnostics
2. **Monitor lease stream**: Track if `_leaseCall` is faulted/completed
3. **Add try-catch to heartbeat loop**: Log failures, set flag for diagnostics
4. **Expose client health**: New method `GetClientHealth()` checking all internal state
5. **Check database file lock**: Identify who holds the lock, detect zombies

### Medium Priority

6. **Track in-flight requests**: Count and age of pending calls
7. **Circuit breaker**: After N consecutive failures, fast-fail for cooldown period
8. **Lease stream as heartbeat**: Remove application-level beats, rely on stream + gRPC keepalive

### Low Priority

9. **Connection pool metrics**: If possible, expose pool utilization
10. **Launch file lock**: Prevent race condition with lockfile

---

## Key Files

| File | Relevant Failure Modes |
|------|------------------------|
| `RepoQlClient.cs` | C1-C7, heartbeat loop, reconnection |
| `SelfTestRunner.cs` | Detection implementation |
| `LeaseRegistry.cs` | Host-side lease tracking |
| `IdleShutdownHostedService.cs` | Lease eviction, idle shutdown |
| `RepoQlServiceImpl.cs` | Query execution, lease handling |

---

## Platform-Specific Failure Modes

### Windows

| Issue | Symptom | Notes |
|-------|---------|-------|
| AF_UNIX only since Build 17063 | Socket creation fails on old Windows | Windows 10 1803+ required |
| Only SOCK_STREAM supported | No datagram sockets | Not an issue for gRPC |
| No abstract sockets | Must use filesystem path | Different from Linux |
| No socketpair() | Can't create paired sockets | Not used by RepoQL |
| Path length limit ~108 chars | Socket creation fails | Same as Unix |
| [Frame too large / PROTOCOL_ERROR](https://github.com/grpc/grpc-go/issues/7039) | "http2: frame too large", transport closure | Windows 10/11 specific with gRPC-Go |
| No WSL↔Windows socket interop | Can't share sockets between WSL and native Windows | Requires workaround |

### WSL-Specific

| Issue | Symptom | Notes |
|-------|---------|-------|
| [DrvFS sockets not shared](https://github.com/microsoft/WSL/issues/3643) | Socket on /mnt/c not accessible from Windows | Critical for RepoQL |
| [AF_UNIX interop broken in WSL2](https://github.com/microsoft/WSL/issues/5961) | Sockets on Windows mount fail | Regression from WSL1 |
| Socket path determines interop | DrvFS path → Windows only; LxFS path → WSL only | Must choose one |
| First operation after creation matters | bind/connect first, or socket becomes WSL-exclusive | Subtle bug source |
| [Colon in path creates ADS](https://github.com/microsoft/WSL/issues/3371) | Socket path with `:` creates Alternate Data Stream | Avoid colons |

**RepoQL's WSL workaround**: Write actual socket path to `.repoql/socket.path` file, clients read that first.

### Linux

| Issue | Symptom | Notes |
|-------|---------|-------|
| Abstract sockets (start with `\0`) | Not portable to Windows/macOS | RepoQL uses filesystem sockets |
| SELinux/AppArmor | Permission denied on socket | Security policy blocks access |
| Path length 108 chars | Socket creation fails | Check in diagnostics |
| [Long connect timeout (~3 min)](https://github.com/dotnet/runtime/issues/66297) | Pending connection hangs | Set explicit ConnectTimeout |
| Stale socket after SIGKILL | Socket file remains, connection refused | Need stale detection |

### macOS

| Issue | Symptom | Notes |
|-------|---------|-------|
| Path length 104 chars (shorter!) | Socket creation fails | More restrictive than Linux |
| Sandbox restrictions | Permission denied | App sandbox blocks socket access |
| SIP restrictions | Can't create sockets in protected paths | Use user-writable paths |

---

## gRPC/HTTP/2 Failure Modes

### Connection-Level

| Issue | Symptom | Detection |
|-------|---------|-----------|
| [GOAWAY with connection reset](https://groups.google.com/g/grpc-io/c/3QylKl4Kr3g) | "Connection reset by peer", broken pipe | RpcException with Unavailable |
| [Double GOAWAY](https://github.com/grpc/grpc-go/issues/6019) | Server sends RST instead of FIN | Connection unusable |
| [RST_STREAM on idle stream](https://github.com/grpc/grpc/issues/19655) | PROTOCOL_ERROR, connection closed | Violates RFC 7540 |
| MAX_CONCURRENT_STREAMS exceeded | Requests queue or fail | Monitor stream count |

### .NET SocketsHttpHandler Specific

| Issue | Symptom | Mitigation |
|-------|---------|------------|
| [Infinite ConnectTimeout default](https://github.com/dotnet/runtime/issues/81989) | Connection attempt hangs forever | Set `ConnectTimeout` explicitly |
| [Connection pool partitioning](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) | MaxConnectionsPerServer × pools = actual limit | Understand pool keys |
| [Stale pooled connections](https://www.stevejgordon.co.uk/httpclient-connection-pooling-in-dotnet-core) | Requests fail after idle period | Set `PooledConnectionLifetime` |
| [Negotiate auth pool issues on Unix](https://github.com/dotnet/runtime/issues/30307) | Wrong identity used | Not applicable (no auth) |

### RepoQL's Current Configuration

```csharp
PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
PooledConnectionLifetime = TimeSpan.FromMinutes(5),
MaxConnectionsPerServer = 10,
KeepAlivePingDelay = TimeSpan.FromSeconds(60),
KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
```

**Potential issues**:
- 60s keepalive delay may be too long to detect dead connections quickly
- 5-minute connection lifetime could cause issues if host restarts

---

## Stale Socket Detection

### The Problem

When a process dies without cleanup (SIGKILL, crash, OOM), the socket file remains but nothing is listening.

### Detection Approaches

| Method | Pros | Cons |
|--------|------|------|
| Connect attempt | Simple, cross-platform | Can't distinguish "starting" from "dead" |
| Check PID file | Fast | PID can wrap, process can be zombie |
| `kill(pid, 0)` | Low overhead | Doesn't work if PID file missing |
| flock on socket | Definitive | Not all platforms support |
| Connect + timeout | Distinguishes hung from dead | Slow (timeout delay) |

### RepoQL's Current Approach

1. Try connect with short timeout
2. If fails, check if host process exists (via stored PID)
3. If no host, rename socket to `.stale.{guid}` atomically
4. Verify no new socket appeared (race check)
5. Delete the renamed file

**Gap**: No protection against PID wraparound (rare but possible).

---

## Mitigations and Patterns

### Circuit Breaker

After N consecutive failures, fail fast without attempting connection.

| Parameter | Suggested Value | Rationale |
|-----------|-----------------|-----------|
| Failure threshold | 3-5 failures | Balance detection vs false positives |
| Break duration | 5-30 seconds | Match expected recovery time |
| Half-open probe | 1 request | Test if service recovered |

**When to use**: Prevent repeated connection attempts to dead host.

**Source**: [Polly Circuit Breaker](https://www.pollydocs.org/strategies/circuit-breaker.html)

---

### Retry with Jitter

Exponential backoff prevents thundering herd on recovery.

```
delay = min(cap, base * 2^attempt) * random(0.8, 1.2)
```

| Parameter | gRPC Default | Notes |
|-----------|--------------|-------|
| Initial backoff | 1s | |
| Multiplier | 1.6 | |
| Max backoff | 120s | |
| Jitter | ±20% | Full jitter performs best |

**Source**: [AWS Architecture - Exponential Backoff and Jitter](https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/)

---

### Keepalive Configuration

Without keepalive, dead connections take ~15 minutes to detect (TCP retransmission timeout).

| Setting | Current | Recommended | Rationale |
|---------|---------|-------------|-----------|
| KeepAlivePingDelay | 60s | 30s | Faster dead connection detection |
| KeepAlivePingTimeout | 30s | 20s | Match gRPC default |
| PooledConnectionLifetime | 5m | 5m | Force periodic reconnection |

**Critical**: Server must accept pings at this rate. Default `MIN_RECV_PING_INTERVAL` is 5 minutes.

**Source**: [gRPC Keepalive Guide](https://grpc.github.io/grpc/core/md_doc_keepalive.html)

---

### Stream-Based Liveness (Alternative to Heartbeats)

Uber's pattern: Server sends heartbeat on data stream every 4-5s. Client assumes disconnection if no message within 7s.

**Advantages**:
- No separate health check channel
- Detects head-of-line blocking
- Natural fit for lease stream

**Current RepoQL approach**: Application-level heartbeats every 10s. Could simplify to stream presence = liveness.

**Source**: [Uber RAMEN Platform](https://www.uber.com/blog/ubers-next-gen-push-platform-on-grpc/)

---

### Health Check Protocol

gRPC standard health checking:

```protobuf
service Health {
  rpc Check(HealthCheckRequest) returns (HealthCheckResponse);
  rpc Watch(HealthCheckRequest) returns (stream HealthCheckResponse);
}
```

| Status | Meaning |
|--------|---------|
| SERVING | Healthy |
| NOT_SERVING | Unhealthy |
| SERVICE_UNKNOWN | Service not registered |

**RepoQL has**: gRPC health check on `repoql.v1.RepoQL` service.

**Source**: [gRPC Health Checking Protocol](https://github.com/grpc/grpc/blob/master/doc/health-checking.md)

---

### systemd-style Readiness

Pattern from systemd `sd_notify`:

| Signal | Meaning |
|--------|---------|
| `READY=1` | Service initialized, accepting connections |
| `WATCHDOG=1` | Still alive (periodic) |
| `STOPPING=1` | Shutting down |

**Applicability**: RepoQL could emit similar signals for diagnostics. "Ready" = initial scan complete. "Watchdog" = indexer responsive.

**Source**: [systemd sd_notify](https://www.freedesktop.org/software/systemd/man/sd_notify.html)

---

### Process Death Detection

| Method | Pros | Cons |
|--------|------|------|
| `Process.Exited` event | Simple | Unreliable in .NET |
| Poll `HasExited` | Works | Overhead |
| PID file + `kill(pid, 0)` | Low overhead | PID wraparound |
| `pidfd_open()` (Linux 5.3+) | Race-free | Linux-only |
| Job Objects (Windows) | Reliable | Windows-only |
| `PR_SET_PDEATHSIG` (Linux) | Child dies with parent | Linux-only |

**Current RepoQL**: Stores host PID, checks `HasExited`. Vulnerable to PID wraparound (rare).

**Source**: [Process management research](../cross-platform-process-management.md)

---

### Stale Socket Cleanup

Recommended sequence:

1. Try `connect()` with short timeout
2. If `ECONNREFUSED` → socket is stale, safe to delete
3. If connect succeeds → another process owns it
4. If timeout → process may be starting

**Current RepoQL**: Renames to `.stale.{guid}`, verifies no new socket, deletes.

---

## Known Limitations of Chosen Architecture

### grpc-dotnet Specific

| Limitation | Impact | Workaround |
|------------|--------|------------|
| No `ConnectAsync()` with Unix sockets | Can't explicitly test connectivity | Make RPC call to test |
| No connectivity state tracking | Can't monitor channel state | Check `_channel.State` manually (limited) |
| No server-side `MaxConnectionAge` | Can't force client reconnection | Client-side `PooledConnectionLifetime` |
| Graceful shutdown doesn't notify clients | Clients don't know server is stopping | Use `KillAsync()` or accept brief failures |

**Source**: [grpc-dotnet Issue #2428](https://github.com/grpc/grpc-dotnet/issues/2428)

### Platform Limitations

| Platform | Limitation | Impact |
|----------|------------|--------|
| Windows | No `socketpair()`, no `SCM_RIGHTS` | Can't pass file descriptors |
| Windows | No `SOCK_DGRAM` for Unix sockets | Must use stream sockets |
| macOS | 104 char path limit (not 108) | Shorter socket paths required |
| macOS | `FileStream.Lock` unsupported | Can't use file locking for coordination |
| Linux | Abstract sockets have no permissions | Security concern if used |
| WSL2 | AF_UNIX on DrvFS broken | Must use LxFS paths or workaround |

---

## Summary: What RepoQL Should Probe

| Probe | Detects | Priority |
|-------|---------|----------|
| Socket file exists | H1 (never started) | High |
| Socket connect (fresh) | H2 (crashed), stale socket | High |
| Host PID alive | H2 (crashed), zombie | High |
| gRPC health check | H4 (unhealthy) | High |
| **Database file lock** | H6, zombie processes | High |
| **Cached channel state** | C1 (stuck channel) | **Critical** |
| **Lease stream state** | C3 (silent close) | **Critical** |
| Socket path length | Platform limit exceeded | Medium |
| Host stderr tail | Crash cause | Medium |
| In-flight request count | C5 (stuck requests) | Medium |
| Connection age | Stale connection | Low |

---

## References

- `docs/flows/host-client-architecture.md` — Architecture overview
- `docs/north-star/reliability.md` — Reliability principles
- `docs/north-star/diagnostics.md` — Diagnostic north star

### Detailed Research (Generated)

- `docs/research/grpc-connection-lifecycle-failure-modes.md` — Channel states, GOAWAY, keepalive
- `docs/research/unix-domain-socket-failure-modes.md` — Platform-specific socket issues
- `docs/research/cross-platform-process-management.md` — Process lifecycle patterns
- `docs/research/service-state-detection-patterns.md` — How Docker, VS Code, etc. do it

### External Sources

- [AF_UNIX comes to Windows](https://devblogs.microsoft.com/commandline/af_unix-comes-to-windows/) — Windows Unix socket support
- [Windows/WSL Interop with AF_UNIX](https://devblogs.microsoft.com/commandline/windowswsl-interop-with-af_unix/) — WSL socket interop
- [Inter-process communication with gRPC and Unix domain sockets](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds?view=aspnetcore-9.0) — Microsoft Learn
- [HttpClient guidelines for .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) — Connection pooling
- [gRPC .NET Unix socket incompatibility with Go](https://github.com/dotnet/aspnetcore/issues/47043) — Known interop issue
- [SocketsHttpHandler timeout issues](https://github.com/dotnet/runtime/issues/81989) — .NET 6 connection pooling

---

*Last updated based on code analysis and platform research. Verify against current implementation.*
