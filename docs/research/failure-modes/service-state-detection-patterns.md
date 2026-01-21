---
description: Research on how similar systems detect and report background service state, informing RepoQL's gRPC-over-Unix-socket architecture
tags: [service-discovery, health-check, gRPC, unix-socket, daemon-management]
audience: { human: 40, agent: 60 }
purpose: { research: 90, reference: 10 }
---

# Service State Detection Patterns

Research for RepoQL service state detection design decisions.

*Research date: 2026-01-21*

## Context

RepoQL uses a gRPC-over-Unix-socket architecture where a background host process must be detected, health-checked, and potentially auto-launched by clients. This research examines how established systems solve similar problems.

**Scope**: Five systems examined across four dimensions each:
- State detection mechanism
- Health check approach
- Auto-launch/restart behavior
- Partial failure handling

---

## Docker / containerd

### State Detection

Docker CLI communicates with dockerd via Unix socket at `/var/run/docker.sock`. The socket acts as the primary indicator of daemon availability.

> [GeeksforGeeks - Docker Tips about /var/run/docker.sock](https://www.geeksforgeeks.org/devops/docker-tips-about-varrundockersock/) - "By default the Docker daemon listens on a UNIX socket located at /var/run/docker.sock for incoming HTTP requests"

The CLI attempts socket connection; failure produces the canonical error "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?"

> [Docker Forums](https://forums.docker.com/t/tutorial-solve-the-error-message-is-the-docker-daemon-running/145891) - Socket existence check is the primary detection mechanism

### Health Check

Docker exposes a `/_ping` endpoint via its REST API:

```bash
curl --unix-socket /var/run/docker.sock http://localhost/_ping
```

Returns "OK" if daemon is responsive.

> [Nathan LeClaire](https://nathanleclaire.com/blog/2015/11/12/using-curl-and-the-unix-socket-to-talk-to-the-docker-api/) - Direct REST API communication over Unix socket for health verification

containerd (underlying runtime) uses gRPC over socket (`containerd.sock`). Health is verified via `ctr plugin ls` showing plugin status, or via Prometheus metrics endpoint.

> [Povilas Versockas - How to Monitor Containerd](https://povilasv.me/how-to-monitor-containerd/) - "Containerd exposes metrics in Prometheus format"

### Auto-Launch/Restart

Docker itself does not auto-launch. On Linux, systemd manages dockerd lifecycle:

```ini
[Service]
Restart=on-failure
RestartSec=5
```

On macOS/Windows, Docker Desktop manages the daemon lifecycle via its own supervisor process.

> [Docker Docs - dockerd](https://docs.docker.com/reference/cli/dockerd/) - Daemon lifecycle is external to Docker itself

### Partial Failure Handling

Docker distinguishes between:
- **Daemon unavailable**: Socket connection fails entirely
- **API timeout**: `context deadline exceeded` errors when daemon is overloaded
- **Container runtime errors**: OCI runtime failures don't crash the daemon

> [LabEx - Troubleshooting Docker API Context Deadline Exceeded](https://labex.io/tutorials/docker-troubleshooting-docker-api-context-deadline-exceeded-errors-413831) - "If the Docker daemon cannot complete the requested operation within this timeframe, the client receives a 'context deadline exceeded' error"

The daemon remains available even when individual container operations fail. This isolation prevents cascading failures.

---

## VS Code Remote / Extension Host

### State Detection

VS Code Remote SSH uses multiple signals:
1. **PID file**: Checks for existing server data file with commit ID
2. **Log file inspection**: Looks for running server indicators
3. **Port probe**: Verifies server is listening on designated port

> [VS Code Docs - Remote Development](https://code.visualstudio.com/docs/remote/ssh) - "VS Code will keep you up-to-date using a progress notification and you can see a detailed log in the Remote - SSH output channel"

The extension maintains state files on the remote host at `~/.vscode-server/`.

### Health Check

No formal health protocol. Detection is connection-based:
- SSH tunnel establishment
- Server process responsiveness via internal protocol messages
- Socket timeout events trigger reconnection

> [GitHub - vscode-remote-release #10122](https://github.com/microsoft/vscode-remote-release/issues/10122) - "Reconnection process is triggered when a socket timeout event is received"

### Auto-Launch/Restart

**Launch**: Server is automatically installed/updated on first connection. The extension downloads and starts vscode-server matching the client version.

> [VS Code Docs - Remote Extensions](https://code.visualstudio.com/api/advanced-topics/remote-extensions) - "The server is automatically installed (or updated) by the Remote Development or GitHub Codespaces extensions when you open a folder"

**Restart**: Limited automatic reconnection (up to 8 retries by default). After extended disconnection (~30 min), requires manual window reload.

> [GitHub - vscode-remote-release #10122](https://github.com/microsoft/vscode-remote-release/issues/10122) - "Remote SSH can reconnect to the server automatically in some situations that retries no more than 8 times"

**Cleanup**: Manual command "Remote-SSH: Uninstall VS Code Server from Host" kills processes and removes installation.

### Partial Failure Handling

Extension host crashes require full window reload. No isolated recovery.

> [GitHub - vscode #32768](https://github.com/Microsoft/vscode/issues/32768) - "When the extension host crashes, the only way to recover is by reloading the window"

Feature requests for automatic restart exist since 2017 but remain unimplemented. The architecture was "built for simpler tools" and struggles with resource-intensive operations.

> [GitHub - vscode #79782](https://github.com/microsoft/vscode/issues/79782) - Community requests for auto-restart functionality remain open

---

## Language Server Protocol (LSP)

### State Detection

LSP uses JSON-RPC 2.0 over stdio, sockets, or named pipes. Server lifecycle is client-managed.

> [LSP Specification 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) - "The lifecycle of a server is managed by the client"

Detection is implicit: if the client spawned the process and it responds to `initialize`, it exists.

### Health Check

No dedicated health endpoint. The `initialize` request serves as implicit health verification:

**Request flow**:
1. Client sends `initialize` with `InitializeParams` (processId, capabilities, rootUri)
2. Server responds with `InitializeResult` (capabilities, serverInfo)
3. Client sends `initialized` notification

> [LSP Specification 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) - "Until the server has responded to the initialize request with an InitializeResult, the client must not send any additional requests or notifications"

Pre-initialization requests receive error code `-32002`. The server should drop notifications except `exit`.

**Capability negotiation** prevents feature mismatch failures:

> [Alibaba Cloud - LSP Quick Start](https://www.alibabacloud.com/blog/quick-start-to-vscode-plug-ins-language-server-protocol-lsp_595294) - "Clients should ignore server capabilities they don't understand"

### Auto-Launch/Restart

Editors implement their own policies:

| Editor | Restart Behavior |
|--------|------------------|
| VS Code | Manual via command palette; some servers have retry limits (5 crashes in 3 minutes) |
| Neovim | Manual `:lua vim.lsp.stop_client()` then `:edit`; no built-in restart API |
| Emacs lsp-mode | Manual `lsp-restart` command; feature request for automatic restart exists |

> [GitHub - neovim #13946](https://github.com/neovim/neovim/issues/13946) - "LSP lacks a first-class way to restart servers"

> [GitHub - emacs-lsp/lsp-mode #285](https://github.com/emacs-lsp/lsp-mode/issues/285) - "If a language server crashes, lsp-mode should automatically restart it"

### Partial Failure Handling

LSP defines graceful shutdown:
1. Client sends `shutdown` request
2. Server responds `null`, stops processing (except `exit`)
3. Client sends `exit` notification
4. Server terminates (exit code 0 if shutdown received, 1 otherwise)

> [LSP Specification 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) - "If the server receives exit without a prior shutdown, it should exit with a non-zero status"

Crash recovery is editor-dependent, not protocol-defined.

---

## systemd

### State Detection

systemd provides multiple state detection mechanisms:

**Socket existence**: For socket-activated services, the socket file indicates systemd is listening.

**Unit state query**:
```bash
systemctl is-active myservice.service
```

**sd_notify()**: Services report state transitions via the notification socket (`$NOTIFY_SOCKET`).

> [man7.org - sd_notify](https://www.man7.org/linux/man-pages/man3/sd_notify.3.html) - "sd_notify tells the service manager that service startup is finished"

### Health Check

**Type=notify services**: Must send `READY=1` to indicate startup complete.

> [freedesktop.org - sd_notify](https://www.freedesktop.org/software/systemd/man/latest/sd_notify.html) - "Since there is little value in signaling non-readiness, the only value services should send is READY=1"

**Watchdog**: Services configured with `WatchdogSec=` must periodically send `WATCHDOG=1`.

```ini
[Service]
Type=notify
WatchdogSec=30s
```

> [freedesktop.org - systemd.service](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html) - "The service must call sd_notify regularly with WATCHDOG=1. If the time between two such calls is larger than the configured time, then the service is placed in a failed state"

**Additional signals**:
- `STATUS=<text>` - Human-readable status (e.g., "Completed 66% of file system check")
- `RELOADING=1` - Configuration reload in progress
- `STOPPING=1` - Service shutting down

### Auto-Launch/Restart

**Socket activation**: systemd listens on socket; spawns service on first connection.

> [0pointer.de - Socket Activation](http://0pointer.de/blog/projects/socket-activation.html) - "A service would be made available lazily when it was required"

```ini
# myservice.socket
[Socket]
ListenStream=/run/myservice.sock

# myservice.service
[Service]
ExecStart=/usr/bin/myservice
```

Only `.socket` unit needs enabling; `.service` starts on demand.

> [ilManzo - Systemd Socket Activation](https://ilmanzo.github.io/post/systemd-socket-activated-services/) - "Socket units may be used to implement on-demand starting of services"

**Restart policies**:

| Restart= | Triggers |
|----------|----------|
| no | Never restart |
| on-success | Exit code 0 |
| on-failure | Non-zero exit, signal, timeout, watchdog |
| on-abnormal | Signal, timeout, watchdog |
| on-watchdog | Watchdog timeout only |
| always | Any termination |

> [freedesktop.org - systemd.service](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html) - "on-failure will restart the service when the process exits with a non-zero exit code or is terminated by a signal"

**Rate limiting**: `StartLimitIntervalSec=` and `StartLimitBurst=` prevent restart storms.

### Partial Failure Handling

systemd isolates failures per-unit. A service crash doesn't affect other services or systemd itself.

**Watchdog escalation**: Configurable response to watchdog failures:
```ini
WatchdogSec=30s
Restart=on-watchdog
StartLimitBurst=4
StartLimitInterval=5min
StartLimitAction=reboot-force
```

> [DoHost - Service Recovery in systemd](https://dohost.us/index.php/2025/10/27/implementing-service-recovery-and-restart-policies-in-systemd/) - "Set the Restart directive to an appropriate value, configure RestartSec to prevent rapid restart loops"

---

## PostgreSQL / MySQL

### State Detection

**PostgreSQL**: `pg_isready` utility checks connection status.

```bash
pg_isready -h localhost -p 5432
```

Exit codes:
| Code | Meaning |
|------|---------|
| 0 | Accepting connections normally |
| 1 | Rejecting connections (e.g., startup) |
| 2 | No response to connection attempt |
| 3 | No attempt made (invalid parameters) |

> [PostgreSQL Docs - pg_isready](https://www.postgresql.org/docs/current/app-pg-isready.html) - "pg_isready is a utility for checking the connection status of a PostgreSQL database server"

**libpq state machine**: `PQstatus()` returns connection state.

| State | Meaning |
|-------|---------|
| CONNECTION_OK | Ready for queries |
| CONNECTION_BAD | Connection failed |
| CONNECTION_STARTED | Waiting for connection |
| CONNECTION_MADE | Connected, waiting to send |
| CONNECTION_AUTH_OK | Authenticated, waiting for backend |
| CONNECTION_SSL_STARTUP | Negotiating SSL |

> [PostgreSQL Docs - Connection Status Functions](https://www.postgresql.org/docs/current/libpq-status.html) - "Only two of these are seen outside of an asynchronous connection procedure: CONNECTION_OK and CONNECTION_BAD"

**MySQL**: `mysqladmin ping` checks server availability.

```bash
mysqladmin ping -h 127.0.0.1
```

Returns 0 if server running (even with access denied), 1 if not running.

> [NeoMind Labs - MySQL Container Health](https://www.neomindlabs.com/blog/how-you-doin-the-quirks-of-checking-mysql-container-health) - "mysqladmin ping is not a reliable way to check readiness"

### Health Check

**PostgreSQL**: `pg_isready` is lightweight but confirms only process availability.

For deeper verification:
```bash
pg_isready -U postgres && psql -U postgres -c 'SELECT 1'
```

> [DEV Community - MySQL Health Check](https://dev.to/samuelko123/health-check-for-mysql-in-a-docker-container-3m5a) - "A better health check is to send an actual SQL query to ensure the database is ready"

**MySQL "temporary server" problem**: During initialization, MySQL runs a bootstrap server that responds to ping but cannot accept external connections.

> [GitHub - docker-library/mysql #930](https://github.com/docker-library/mysql/issues/930) - "The MySQL container may appear 'up' from Docker's perspective, because even the temporary server will respond correctly to the ping request"

**MariaDB healthcheck.sh**: More robust verification:
```bash
healthcheck.sh --connect --innodb_initialized
```

### Auto-Launch/Restart

Databases do not auto-launch themselves. Supervisors (systemd, Docker, Kubernetes) manage lifecycle.

**Docker Compose pattern**:
```yaml
healthcheck:
  test: ["CMD", "pg_isready", "-U", "postgres"]
  interval: 5s
  timeout: 5s
  retries: 5
  start_period: 10s
```

> [Last9 - Docker Compose Health Checks](https://last9.io/blog/docker-compose-health-checks/) - "Docker Compose will only start the api container after the database has passed its health check"

**Client-side reconnection**: libpq and MySQL connectors provide `ping()` methods with optional auto-reconnect.

> [MySQL Docs - MySQLConnection.ping()](https://dev.mysql.com/doc/connector-python/en/connector-python-api-mysqlconnection-ping.html) - "When reconnect is set to True, one or more attempts are made to try to reconnect"

### Partial Failure Handling

Databases distinguish:
- **Connection failures**: Client-side, may be transient
- **Query failures**: Logged but don't crash server
- **Backend crashes**: Individual connection terminates, others continue

libpq requires cleanup even on failure:

> [PostgreSQL Docs - Connection Control](https://www.postgresql.org/docs/current/libpq-connect.html) - "Even if the server connection attempt fails, the application should call PQfinish to free the memory used by the PGconn object"

---

## gRPC Health Checking Protocol

gRPC defines a standard health checking protocol applicable to any gRPC service.

### Proto Definition

```protobuf
syntax = "proto3";
package grpc.health.v1;

message HealthCheckRequest {
  string service = 1;
}

message HealthCheckResponse {
  enum ServingStatus {
    UNKNOWN = 0;
    SERVING = 1;
    NOT_SERVING = 2;
    SERVICE_UNKNOWN = 3;
  }
  ServingStatus status = 1;
}

service Health {
  rpc Check(HealthCheckRequest) returns (HealthCheckResponse);
  rpc Watch(HealthCheckRequest) returns (stream HealthCheckResponse);
}
```

> [gRPC - Health Checking](https://grpc.io/docs/guides/health-checking/) - "A gRPC service is used as the health checking mechanism for both simple client-to-server scenarios and other control systems"

### Serving Status Semantics

| Status | Meaning |
|--------|---------|
| UNKNOWN | Initial or indeterminate |
| SERVING | Ready to accept requests |
| NOT_SERVING | Unavailable or shutting down |
| SERVICE_UNKNOWN | Unregistered service (Watch only) |

> [GitHub - grpc/grpc health-checking.md](https://github.com/grpc/grpc/blob/master/doc/health-checking.md) - "The server should register all the services manually and set the individual status"

**Empty service name** represents overall server health.

### Check vs Watch

**Check**: Unary RPC for point-in-time status. Client should set deadline.

**Watch**: Streaming RPC for continuous monitoring. Server sends immediate status, then updates on change.

> [GitHub - grpc/grpc health-checking.md](https://github.com/grpc/grpc/blob/master/doc/health-checking.md) - "A client can call the Watch method to perform a streaming health-check"

### gRPC + systemd Socket Activation

gRPC supports systemd socket activation. Service receives pre-bound listening socket via `sd_listen_fds()`.

> [GitHub - grpc/grpc systemd_socket_activation](https://github.com/grpc/grpc/blob/master/examples/cpp/systemd_socket_activation/README.md) - Example showing gRPC server with systemd socket activation

---

## Cross-Platform Considerations

### Unix Sockets vs Named Pipes

| Mechanism | Platform | Notes |
|-----------|----------|-------|
| Unix Domain Socket | Linux, macOS, Windows 10 1803+ | Lower latency (~30us RTT), higher throughput |
| Named Pipes | Windows | Kernel objects (`\\.\pipe\name`), support network access |

> [GitHub - dotnet/runtime #14633](https://github.com/dotnet/runtime/issues/14633) - "Windows lacks native Unix domain sockets (added in Windows 10 1803+)"

> [Linux Vox - IPC Comparison](https://linuxvox.com/blog/sockets-on-same-machine-for-windows-and-linux/) - "Unix Domain Sockets are dominant in all scenarios with lower latency"

gRPC supports Unix sockets natively. Named pipe support is a feature request.

> [GitHub - grpc/grpc #13447](https://github.com/grpc/grpc/issues/13447) - "Named pipes are very similar to Unix domain sockets"

---

## Comparison

| System | State Detection | Health Check | Auto-Launch | Partial Failure |
|--------|-----------------|--------------|-------------|-----------------|
| Docker | Socket existence + `/_ping` | REST endpoint | External (systemd) | API/runtime isolated |
| VS Code Remote | PID file + port probe | Connection-based | On-demand install | Window reload required |
| LSP | Process existence + `initialize` | Capability exchange | Editor-managed | Editor-dependent |
| systemd | Unit state + `sd_notify` | Watchdog ping | Socket activation | Per-unit isolation |
| PostgreSQL | `pg_isready` + `PQstatus()` | Query execution | External | Connection isolation |
| gRPC standard | Socket connection | `grpc.health.v1.Health` | Socket activation | Service-level status |

---

## Gaps

**Not determined**:
- VS Code Remote internal protocol specifics (proprietary)
- Exact retry/backoff algorithms in production deployments
- Performance characteristics of each approach under load
- Memory overhead of streaming health checks (Watch) vs polling (Check)

**Requires further investigation**:
- How Docker Desktop on Windows manages dockerd lifecycle
- Named pipe health checking patterns for Windows-first services
- Cross-process memory sharing for state synchronization

---

## Sources Summary

| Source | Type | What it establishes |
|--------|------|---------------------|
| [Docker Docs](https://docs.docker.com/reference/cli/dockerd/) | Official docs | Daemon socket configuration |
| [PostgreSQL Docs](https://www.postgresql.org/docs/current/app-pg-isready.html) | Official docs | pg_isready exit codes |
| [LSP Specification 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) | Official spec | Lifecycle protocol |
| [gRPC Health Checking](https://grpc.io/docs/guides/health-checking/) | Official docs | Health protocol definition |
| [freedesktop.org sd_notify](https://www.freedesktop.org/software/systemd/man/latest/sd_notify.html) | Official docs | Notification protocol |
| [VS Code Remote Docs](https://code.visualstudio.com/docs/remote/ssh) | Official docs | Remote server architecture |
| GitHub Issues | Community | Feature gaps, real-world failures |
