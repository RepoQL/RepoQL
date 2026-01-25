# Host Crashed

Host process dies unexpectedly during active session.

## Trigger

Client has active lease, host process terminates unexpectedly.

## Stages

### 1. RPC Failure

**Actor**: MCP Client (RPC layer)
**Action**: RPC fails with `Unavailable` or `Internal` status code
**Output**: gRPC exception with status code
**Failure**: Channel may cache stale connection state

### 2. Socket State Check

**Actor**: MCP Client (connection logic)
**Action**: Check socket file exists (may be stale)
**Output**: Socket present but stale
**Failure**: N/A

### 3. PID Verification

**Actor**: MCP Client (connection logic)
**Action**: Check PID file, verify process exited
**Output**: Exit code if available
**Failure**: N/A

### 4. Stderr Capture

**Actor**: MCP Client (diagnostics)
**Action**: Read captured stderr from host process
**Output**: Crash reason (exception, OOM, signal)
**Failure**: Stderr may be empty if crash was immediate

### 5. Channel Cleanup

**Actor**: MCP Client (connection logic)
**Action**: Dispose cached gRPC channel
**Output**: Channel resources released
**Failure**: N/A

### 6. Reconnection

**Actor**: MCP Client (connection logic)
**Action**: Delete stale socket, launch new host
**Output**: Fresh host process
**Failure**: Same as "Host Not Running" flow

## Termination

Flow completes when:
- New host running and lease established, OR
- Repeated crashes trigger circuit breaker

## Flow Diagram

```mermaid
flowchart TD
    Start([RPC in progress]) --> HostDies["Host crashes"]

    HostDies --> RpcFails["RPC fails: Unavailable/Internal"]

    RpcFails --> IsReconnectable{Reconnectable error?}

    IsReconnectable -->|"Yes (Unavailable, IOException)"| DisposeChannel["Dispose cached channel"]
    IsReconnectable -->|"No (InvalidArgument, etc)"| PropagateError["Propagate to caller"]:::error

    DisposeChannel --> CheckStderr["Capture stderr"]
    CheckStderr --> Reconnect["Full reconnect flow"]

    Reconnect --> RetryRpc{Retry RPC?}

    RetryRpc -->|"First attempt"| DoRetry["Retry once"]
    RetryRpc -->|"Already retried"| PropagateError

    DoRetry --> Success{Success?}
    Success -->|Yes| Done([Return result]):::success
    Success -->|No| PropagateError

    classDef success fill:#90EE90,stroke:#2E7D32,color:#000
    classDef error fill:#FFB6C1,stroke:#C62828,color:#000

    %% MEANING: Automatic reconnection after host crash
```

## Diagnostic Output

```
❌ Host crashed
   Socket: /repo/.repoql/repoql.sock (stale)
   PID: 12345 (exited, code 139)

   Last stderr:
   > Unhandled exception: OutOfMemoryException
   > at RepoQL.Indexing.BatchProcessor.ProcessBatch()

   → Restarting automatically...
```

## Recovery

| Condition | Action |
|-----------|--------|
| Single crash | Auto-relaunch, retry RPC once |
| Repeated crashes | Circuit breaker, surface pattern |
| OOM crashes | Suggest reducing batch size |

## Status

✅ **Partially implemented** - Auto-relaunch works, but no circuit breaker for repeated crashes.

**Gap**: Repeated crashes cause repeated 120s waits with no circuit breaker.
