---
description: "::host.restart (restart and wait for ready), ::host.stop (graceful shutdown without relaunch) — gRPC host lifecycle"
tags: ["command", "host", "restart", "stop", "server", "shutdown", "reload"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# Host Commands

Manage the gRPC host process lifecycle.

**Restart:** `::host.restart` — stop, relaunch, wait for ready
**Stop:** `::host.stop` — graceful shutdown, no relaunch

---

## ::host.restart

### Capsule: BasicUsage

**Invariant**
`::host.restart` stops the current host and starts a fresh one. Returns timing, PIDs, and startup logs.

**Example**
```
::host.restart
→ Host restarted in 3.2s (previous PID 43876 stopped, new PID 51234).

  Startup logs:
  [host 14:23:01] Host starting (pid=51234 version=1.3.31)
  [host 14:23:01] Phase: preflight
  [host 14:23:02] Phase: socket bind
  [host 14:23:03] Phase: database init
  [host 14:23:04] Phase: ready
  [host 14:23:04] Host ready
```
//BOUNDARY: Shutdown has a 5-second grace period before force kill. The command waits until the new host passes health checks.

**Depth**
- Sends `ShutdownHost` gRPC RPC to the current host
- Waits up to 5 seconds for graceful exit, then kills the process tree
- Reconnects via `GetClientAsync` which auto-launches and health-probes the new host
- Startup logs come from the host's stderr ring buffer (last ~50 lines)
- In debug builds, the new host launches via `dotnet watch` for auto-rebuild on code changes

---

## ::host.stop

### Capsule: BasicUsage

**Invariant**
`::host.stop` leaves the repository host stopped. Returns timing and the previous PID when one was known.

**Example**
```
::host.stop
→ Host stopped in 1.4s (previous PID 51234 stopped).
  Initial host_running: true
```
//BOUNDARY: Shutdown has a 5-second grace period before force kill. The command does not relaunch the host.

**Depth**
- Sends `ShutdownHost` gRPC RPC to the current host when the socket is reachable
- Waits up to 10 seconds for the recorded host PID to exit, then kills it if needed
- Cleans up stale socket state after shutdown
- If the host is already stopped, returns success instead of treating that as an error

---

## Help

```
::host.restart --help
::host.stop --help
```
