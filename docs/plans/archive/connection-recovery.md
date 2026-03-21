# Plan: Connection Recovery

Implements: [Reliability Design](../designs/reliability.md) — Channel State Monitoring

## Scope

**Covers:**
- Channel state checking before RPC calls
- Automatic reconnection when channel is stuck in TransientFailure
- Channel disposal and recreation
- Lease stream error handling and reconnection
- Circuit breaker for repeated host crashes

**Does not cover:**
- Diagnostic data collection (Plan: Diagnostics)
- Host-side logging (Plan: Host Persistence)
- Host startup validation (Plan: Preflight Validation)

## Enables

Once Connection Recovery exists:
- **Stuck channels recover automatically** — no more "restart MCP server" required
- **Transient failures don't persist** — channel recreated when stuck
- **Better reliability** — Unix socket channels recover from host restarts

## Prerequisites

- gRPC channel is created and cached in `RepoQlClient` or similar
- Channel state is accessible via `GrpcChannel.State` property

## North Star

When the host restarts or the channel gets stuck, the client recovers automatically. The user never sees "channel stuck in TransientFailure."

## Done Criteria

### Auto-Launch on Request

- Before each RPC, the client shall check if host is reachable
- When socket doesn't exist, the client shall launch host and wait for SERVING
- When socket exists but not connectable (stale), the client shall launch host
- When host becomes SERVING, the client shall proceed with original request
- The caller sees latency on first request after crash, but request succeeds

### State Checking

- Before each RPC, the client shall check channel connectivity state
- When state is `TransientFailure`, the client shall trigger recovery
- When state is `Shutdown`, the client shall trigger recovery
- When state is `Ready` or `Idle`, the client shall proceed with RPC

### Recovery

- When recovery triggered, the client shall dispose the existing channel
- The client shall create a new channel to the same endpoint
- The client shall retry the original RPC on the new channel
- When new channel also fails, report error (don't loop)

### Integration

- The state check shall be implemented in a single location (not per-RPC)
- Existing RPC call sites shall not need modification
- Recovery shall be transparent to callers

### Health Monitoring

- The client shall use `Watch("")` for immediate state change notifications
- The client shall also call `Check("")` periodically (every 30s) with 5s timeout
- Watch catches active state changes; Check catches deadlocks (host can't push if stuck)
- When Watch receives NOT_SERVING, log warning and collect diagnostic context
- When Watch stream errors (connection lost), trigger channel recovery
- When Check times out, trigger channel recovery (host unresponsive)

### Lease Handling

- When lease stream faults, the client shall attempt to re-establish the lease
- When lease heartbeat times out, the client shall trigger channel recovery
- When re-establishing lease fails, the client shall dispose channel and reconnect

### Circuit Breaker

- The client shall track host crash count (from consecutive failed connections)
- When host crashes 3 times within 5 minutes, the client shall stop auto-relaunching
- When circuit breaker trips, report "Host repeatedly crashing" with recent crash causes
- The circuit breaker shall reset after 5 minutes of no crashes
- Manual `:diagnostics:` shall bypass circuit breaker to show current state

### Logging

- When recovery triggered, log "Channel stuck in {state}, reconnecting"
- When recovery succeeds, log "Channel reconnected"
- When recovery fails, log error with details
- When circuit breaker trips, log "Circuit breaker open: host crashed {n} times"

## Constraints

- **Single retry** — don't retry indefinitely; one reconnect attempt per RPC
- **No backoff on reconnect** — Unix sockets are local, instant retry is fine
- **Dispose old channel** — don't leak channels

## References

- [Reliability Design](../designs/reliability.md) — channel state monitoring decision
- [gRPC Channel State](https://grpc.io/docs/guides/connectivity-state-machine/) — state machine docs
- [MCP Failure Modes](../flows/future/mcp/failure-modes/channel-stuck.md) — detailed scenario

## Error Policy

Recovery is best-effort:
1. Check state before RPC
2. If stuck, try once to reconnect
3. If reconnect fails, propagate the original error
4. Don't mask failures with infinite retries
