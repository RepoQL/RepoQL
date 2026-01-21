# gRPC Connection Lifecycle Failure Modes and Mitigations

Research for decisions about gRPC connection lifecycle handling in .NET over Unix sockets.

*Research date: 2026-01-21*

---
description: Research on gRPC channel states, GOAWAY handling, keepalive behavior, reconnection strategies, and SocketsHttpHandler connection pooling failure modes
tags: [grpc, networking, failure-modes, unix-sockets, dotnet]
audience: { human: 40, agent: 60 }
purpose: { research: 90, reference: 10 }
---

## Context

This research investigates gRPC connection lifecycle failure modes specifically for .NET gRPC (grpc-dotnet) over Unix domain sockets. The goal is to understand what triggers state transitions, how failures manifest, and what mitigations exist.

---

## 1. Channel Connectivity States

gRPC defines five connectivity states that form a state machine.

### State Definitions

| State | Meaning |
|-------|---------|
| **IDLE** | Not trying to create a connection; no pending RPCs. Default timeout: 300 seconds. |
| **CONNECTING** | Attempting connection: name resolution, TCP establishment, or TLS handshake in progress. |
| **READY** | Connection established through TLS/protocol handshaking; ready for RPCs. |
| **TRANSIENT_FAILURE** | Transient failure occurred (TCP timeout, socket error). Will retry with exponential backoff. |
| **SHUTDOWN** | Channel shutting down. Terminal state; never leaves. |

> [gRPC Connectivity Semantics](https://grpc.github.io/grpc/core/md_doc_connectivity-semantics-and-api.html) - Official specification

### State Transitions

| From | To | Trigger |
|------|-----|---------|
| IDLE | CONNECTING | New RPC activity |
| CONNECTING | READY | All connection steps succeeded |
| CONNECTING | TRANSIENT_FAILURE | Any connection step failed |
| CONNECTING | IDLE | No RPC activity for IDLE_TIMEOUT |
| READY | TRANSIENT_FAILURE | Failure on established channel |
| READY | IDLE | No RPC for IDLE_TIMEOUT OR GOAWAY with no pending RPCs |
| TRANSIENT_FAILURE | CONNECTING | Backoff period expired |
| Any state | SHUTDOWN | Application-triggered shutdown |

> [grpc/connectivity-semantics-and-api.md](https://github.com/grpc/grpc/blob/master/doc/connectivity-semantics-and-api.md) - GitHub source

### Failure Mode: State Not Updating After Disconnection

| Aspect | Detail |
|--------|--------|
| **Symptom** | `ConnectivityState` remains `Ready` after server disconnects; `WaitForStateChangedAsync()` waits indefinitely |
| **Root Cause** | Graceful server shutdown (`ShutdownAsync`) does not trigger client state change notifications |
| **Detection** | Polling loop on channel state never exits |
| **Mitigation** | Use `KillAsync` instead of `ShutdownAsync` on server; state transitions to `TransientFailure` |

> [grpc-dotnet Issue #1885](https://github.com/grpc/grpc-dotnet/issues/1885) - Client state doesn't change after disconnection

### Failure Mode: Unix Domain Socket Incompatibility with ConnectAsync

| Aspect | Detail |
|--------|--------|
| **Symptom** | Error: "Channel is configured with an HTTP transport doesn't support client-side load balancing or connectivity state tracking" |
| **Root Cause** | `ConnectAsync()` is incompatible with custom `ConnectCallback` used for Unix sockets |
| **Detection** | Exception thrown when calling `GrpcChannel.ConnectAsync()` |
| **Mitigation** | Remove `ConnectAsync()` call entirely; make gRPC calls directly without explicit connectivity tracking |

> [grpc-dotnet Issue #2428](https://github.com/grpc/grpc-dotnet/issues/2428) - Connecting to gRPC Server over Unix Domain Socket

---

## 2. GOAWAY Handling

GOAWAY is an HTTP/2 frame used to initiate graceful connection shutdown or signal serious errors.

### GOAWAY Semantics

The `last-stream-id` field indicates the highest stream ID the server processed. Streams with higher IDs were not processed and should be retried.

| Scenario | Server Behavior | Client Should |
|----------|-----------------|---------------|
| **Graceful shutdown** | Send GOAWAY with `last-stream-id = 2^31-1`, wait one RTT, send second GOAWAY with actual last stream ID | Retry streams above last-stream-id on new connection |
| **Resource exhaustion** | Send GOAWAY with actual last-stream-id | Immediately retry unprocessed streams |
| **Keepalive violation** | Send GOAWAY with ENHANCE_YOUR_CALM and "too_many_pings" debug data | Reduce ping frequency |

> [HTTP/2 in nginx GOAWAY issue](https://trac.nginx.org/nginx/ticket/2224) - Two-stage shutdown explanation

### Failure Mode: Single-Stage GOAWAY Causing Request Failures

| Aspect | Detail |
|--------|--------|
| **Symptom** | RPCs fail with `UNAVAILABLE: HTTP/2 error code: NO_ERROR Received Goaway` |
| **Root Cause** | Server (e.g., nginx) sends single GOAWAY without two-stage graceful shutdown |
| **Detection** | Intermittent failures during server deployments or connection rotation |
| **Mitigation** | Enable retry policy with `StatusCode.Unavailable` in `RetryableStatusCodes` |

> [grpc-java Issue #8310](https://github.com/grpc/grpc-java/issues/8310) - UNAVAILABLE: HTTP/2 error code: NO_ERROR Received Goaway

### Failure Mode: ENHANCE_YOUR_CALM Connection Termination

| Aspect | Detail |
|--------|--------|
| **Symptom** | Connection closed with GOAWAY frame; debug data contains "too_many_pings" |
| **Root Cause** | Client `KEEPALIVE_TIME_MS` is lower than server's `MIN_RECV_PING_INTERVAL_WITHOUT_DATA_MS` |
| **Detection** | Server logs: "Got too many pings from the client, closing the connection"; Client: `http2.ErrCodeEnhanceYourCalm` |
| **Mitigation** | Coordinate keepalive settings between client and server; use 5-minute keepalive as safe default |

> [gRPC Keepalive Guide](https://grpc.github.io/grpc/core/md_doc_keepalive.html) - Server parameters and misconfiguration consequences

---

## 3. Keepalive Behavior

Keepalive uses HTTP/2 PING frames to detect dead connections and keep connections alive through proxies.

### Client-Side Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `GRPC_ARG_KEEPALIVE_TIME_MS` | Disabled (INT_MAX) | Interval between keepalive pings |
| `GRPC_ARG_KEEPALIVE_TIMEOUT_MS` | 20,000 ms | Time to wait for PING acknowledgment |
| `GRPC_ARG_KEEPALIVE_PERMIT_WITHOUT_CALLS` | false | Allow pings when no active RPCs |
| `GRPC_ARG_HTTP2_MAX_PINGS_WITHOUT_DATA` | 2 | Max pings without data frames |

### Server-Side Parameters

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `GRPC_ARG_KEEPALIVE_TIME_MS` | 7,200,000 ms (2 hours) | Server-initiated ping interval |
| `GRPC_ARG_KEEPALIVE_TIMEOUT_MS` | 20,000 ms | Ping acknowledgment timeout |
| `GRPC_ARG_HTTP2_MIN_RECV_PING_INTERVAL_WITHOUT_DATA_MS` | 300,000 ms (5 min) | Minimum acceptable client ping interval |
| `GRPC_ARG_HTTP2_MAX_PING_STRIKES` | 2 | Bad pings before GOAWAY |

> [gRPC Keepalive User Guide](https://grpc.github.io/grpc/core/md_doc_keepalive.html) - All parameters and defaults

### .NET SocketsHttpHandler Keepalive

```csharp
var handler = new SocketsHttpHandler
{
    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
    EnableMultipleHttp2Connections = true
};
```

> [Microsoft Performance Best Practices](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance) - SocketsHttpHandler configuration

### Failure Mode: Silent Connection Death (Zombie Connections)

| Aspect | Detail |
|--------|--------|
| **Symptom** | RPCs hang indefinitely; connection appears valid but server is unreachable |
| **Root Cause** | Without keepalive, TCP retransmits for ~15 minutes before timeout; gRPC trusts TCP state |
| **Detection** | Long delays before UNAVAILABLE errors; metrics show increasing latency |
| **Mitigation** | Enable keepalive (sets TCP_USER_TIMEOUT to ~20 seconds); dead connections detected quickly |

> [TCP and gRPC Failed Connection Timeouts](https://www.evanjones.ca/tcp-connection-timeouts.html) - Detailed analysis of timeout behavior

### Failure Mode: Keepalive Disabled When No Active Calls

| Aspect | Detail |
|--------|--------|
| **Symptom** | Connections die during idle periods despite keepalive configuration |
| **Root Cause** | `KEEPALIVE_PERMIT_WITHOUT_CALLS` defaults to false |
| **Detection** | First RPC after idle period fails with connection error |
| **Mitigation** | Set `KEEPALIVE_PERMIT_WITHOUT_CALLS = true` if idle connections should be maintained |

> [gRPC Keepalive Guide](https://grpc.github.io/grpc/core/md_doc_keepalive.html) - PERMIT_WITHOUT_CALLS behavior

---

## 4. Reconnection Strategies

### Connection Backoff Algorithm

gRPC uses exponential backoff with jitter for reconnection attempts.

| Parameter | Value |
|-----------|-------|
| Initial Backoff | 1 second |
| Multiplier | 1.6 |
| Max Backoff | 120 seconds |
| Jitter | +/- 20% |
| Min Connect Timeout | 20 seconds |

**Reset condition**: Backoff resets to initial value when SETTINGS frame is received (connection accepted).

> [gRPC Connection Backoff Protocol](https://github.com/grpc/grpc/blob/master/doc/connection-backoff.md) - Algorithm specification

### .NET Retry Policy Configuration

```csharp
var retryPolicy = new RetryPolicy
{
    MaxAttempts = 5,
    InitialBackoff = TimeSpan.FromSeconds(1),
    MaxBackoff = TimeSpan.FromSeconds(5),
    BackoffMultiplier = 1.5,
    RetryableStatusCodes = { StatusCode.Unavailable }
};
```

**Retry throttling**: Client tracks `token_count` (starts at `maxTokens`). Failed RPCs decrement by 1; successful RPCs increment by `tokenRatio`. Retries pause when count falls below half of `maxTokens`.

> [Microsoft gRPC Retries](https://learn.microsoft.com/en-us/aspnet/core/grpc/retries) - .NET retry configuration

### Failure Mode: Retry Storm After Partial Outage

| Aspect | Detail |
|--------|--------|
| **Symptom** | Server overwhelmed by retry traffic after recovering from outage |
| **Root Cause** | Many clients retry simultaneously without coordination |
| **Detection** | Server metrics show traffic spike post-recovery |
| **Mitigation** | Use jitter in backoff; enable retry throttling; consider circuit breaker pattern |

### Failure Mode: Committed Call Not Retried

| Aspect | Detail |
|--------|--------|
| **Symptom** | RPC fails but is not retried despite retry policy |
| **Root Cause** | Call was "committed" (server sent response headers, or message exceeded `MaxRetryBufferSize`) |
| **Detection** | Check `grpc-previous-rpc-attempts` metadata; absence indicates no retry occurred |
| **Mitigation** | Understand that retries only work for calls that haven't started server-side processing |

> [gRPC Retry Guide](https://grpc.io/docs/guides/retry/) - When retries occur

---

## 5. SocketsHttpHandler Connection Pooling

### Connection Lifecycle Settings

| Setting | Purpose | Impact on gRPC |
|---------|---------|----------------|
| `PooledConnectionLifetime` | Max age of connection | Forces DNS re-resolution; prevents stale connections |
| `PooledConnectionIdleTimeout` | Idle connection timeout | Frees resources; may increase latency for next request |
| `EnableMultipleHttp2Connections` | Allow multiple HTTP/2 connections | Bypasses concurrent stream limit (~100) |

> [HttpClient Guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) - PooledConnectionLifetime purpose

### HTTP/2 vs HTTP/1.1 Pooling Differences

HTTP/2 connections aren't traditionally "pooled" - there's one connection per destination with stream multiplexing. The naming (`PooledConnectionLifetime`) reflects HTTP/1.1 origins but still applies to HTTP/2 for DNS change propagation.

> [dotnet/runtime Issue #26917](https://github.com/dotnet/runtime/issues/26917) - HTTP2: Support connection lifetime and timeout

### Failure Mode: DNS Changes Not Propagated

| Aspect | Detail |
|--------|--------|
| **Symptom** | Traffic continues to old server IP after DNS update |
| **Root Cause** | DNS only resolved at connection creation; long-lived connections ignore TTL |
| **Detection** | Requests fail or route incorrectly after infrastructure changes |
| **Mitigation** | Set `PooledConnectionLifetime` (e.g., 15 minutes) to force periodic reconnection |

### Failure Mode: Concurrent Stream Limit Exceeded

| Aspect | Detail |
|--------|--------|
| **Symptom** | Calls queue on client side; increased latency under load |
| **Root Cause** | HTTP/2 limits concurrent streams to ~100 per connection |
| **Detection** | Client-side metrics show queueing; latency spikes correlate with concurrent call count |
| **Mitigation** | Set `EnableMultipleHttp2Connections = true` to create additional connections |

> [Microsoft gRPC Performance](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance) - Multiple HTTP/2 connections

---

## 6. Unix Domain Socket Specific Considerations

### Configuration

```csharp
// Server
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenUnixSocket(socketPath, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

// Client
var socketsHandler = new SocketsHttpHandler
{
    ConnectCallback = async (context, token) =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
        return new NetworkStream(socket, ownsSocket: true);
    }
};
```

> [Microsoft UDS Documentation](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds) - Unix domain socket setup

### Known Limitations

| Limitation | Impact |
|------------|--------|
| No client-side load balancing | Cannot distribute RPCs across multiple backends |
| No connectivity state tracking | `ConnectAsync()` throws; cannot monitor channel state |
| No `MaxConnectionAge` server-side | Cannot force connection rotation from server |

> [grpc-dotnet Issue #2308](https://github.com/grpc/grpc-dotnet/issues/2308) - MAX_CONNECTION_AGE not supported

### Failure Mode: Broken Pipe (EPIPE)

| Aspect | Detail |
|--------|--------|
| **Symptom** | Write operations fail with EPIPE/SIGPIPE |
| **Root Cause** | Peer closed socket; sender unaware and attempts write |
| **Detection** | `SIGPIPE` signal or `errno = EPIPE` |
| **Mitigation** | Handle SIGPIPE (ignore signal, check return values); implement reconnection logic |

> [Broken Pipe Wikipedia](https://en.wikipedia.org/wiki/Broken_pipe) - EPIPE semantics

---

## 7. TRANSIENT_FAILURE vs UNAVAILABLE

These are distinct concepts often confused:

| Aspect | TRANSIENT_FAILURE (Channel State) | UNAVAILABLE (Status Code) |
|--------|-----------------------------------|---------------------------|
| **Type** | Channel connectivity state | RPC response code |
| **Scope** | Connection layer | Request layer |
| **Meaning** | TCP/TLS failure occurred | Service temporarily unavailable |
| **Recovery** | Automatic via backoff/reconnect | Configurable via retry policy |
| **Examples** | TCP handshake timeout, socket error | Server shutdown, network issue during RPC |

> [gRPC Status Codes](https://grpc.github.io/grpc/core/md_doc_statuscodes.html) - UNAVAILABLE definition

---

## Gaps

The following could not be definitively determined:

1. **grpc-dotnet server-side keepalive parity**: Server-side `MaxConnectionAge` and `MaxConnectionIdle` are not implemented in grpc-dotnet. Kestrel handles some keepalive, but the mapping to gRPC semantics is unclear.

2. **Unix socket keepalive interaction**: How SocketsHttpHandler keepalive settings interact with Unix domain sockets specifically (vs TCP) was not found in documentation.

3. **Retry behavior with custom ConnectCallback**: Whether retry policies work correctly when using custom `ConnectCallback` for Unix sockets is not documented.

4. **Health checking over Unix sockets**: Whether gRPC health checking protocol works with Unix domain socket transport in .NET is undocumented.

---

## Sources

### Primary Documentation
- [gRPC Connectivity Semantics and API](https://grpc.github.io/grpc/core/md_doc_connectivity-semantics-and-api.html)
- [gRPC Connection Backoff Protocol](https://github.com/grpc/grpc/blob/master/doc/connection-backoff.md)
- [gRPC Keepalive User Guide](https://grpc.github.io/grpc/core/md_doc_keepalive.html)
- [gRPC Status Codes](https://grpc.github.io/grpc/core/md_doc_statuscodes.html)

### Microsoft Documentation
- [Microsoft gRPC Performance Best Practices](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance)
- [Microsoft gRPC Retries](https://learn.microsoft.com/en-us/aspnet/core/grpc/retries)
- [Microsoft Unix Domain Sockets with gRPC](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds)

### Analysis Articles
- [TCP and gRPC Failed Connection Timeouts - Evan Jones](https://www.evanjones.ca/tcp-connection-timeouts.html)
- [gRPC is Easy to Misconfigure - Evan Jones](https://www.evanjones.ca/grpc-is-tricky.html)
- [How gRPC Keepalive Solved Our Zombie Connections - Freshworks](https://medium.com/freshworks-engineering-blog/how-grpc-keepalive-solved-our-zombie-connections-mystery-f4f626c8a9f2)

### GitHub Issues
- [grpc-dotnet #1885 - Client state doesn't change after disconnection](https://github.com/grpc/grpc-dotnet/issues/1885)
- [grpc-dotnet #2428 - Unix Domain Socket ConnectAsync limitation](https://github.com/grpc/grpc-dotnet/issues/2428)
- [grpc-dotnet #2308 - MAX_CONNECTION_AGE not supported](https://github.com/grpc/grpc-dotnet/issues/2308)
- [grpc-java #8310 - GOAWAY handling](https://github.com/grpc/grpc-java/issues/8310)
