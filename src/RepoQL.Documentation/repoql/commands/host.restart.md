---
description: "Restart the gRPC host process. Waits for the new host to be serving and returns startup logs."
tags: ["command", "host", "restart", "server", "reload"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::host.restart

Restart the repository host process. Sends a graceful shutdown, waits for exit (kills if necessary), launches a new host, and returns startup logs.

---

## Capsule: BasicUsage

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

## Help

```
::host.restart --help
→ ::host.restart — Restart the repository host
  Usage: ::host.restart
```
