---
description: "Stop the gRPC host process without relaunching it. Uses graceful shutdown first, then PID fallback if needed."
tags: ["command", "host", "stop", "server", "shutdown"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::host.stop

Stop the repository host process without starting a replacement. Sends a graceful shutdown, waits for exit, kills the process if necessary, and verifies the host is no longer serving.

---

## Capsule: BasicUsage

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
- Verifies the host is no longer serving before returning success
- If the host is already stopped, returns success instead of treating that as an error

---

## Help

```
::host.stop --help
→ ::host.stop — Stop the repository host
  Usage: ::host.stop
```
