---
description: "Run system health diagnostics. Quick checks or full report."
tags: ["command", "diagnostics", "health", "selftest", "status"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::diagnostics

Run system health diagnostics. Reports host connectivity, database status, pipeline state, and service health.

---

## Capsule: BasicUsage

**Invariant**
`::diagnostics` runs a full diagnostic report. `::diagnostics.fast` runs quick checks only.

**Example**
```
::diagnostics.fast
→ Host: healthy (PID 12345, uptime 2h)
  Pipeline: idle
  Database: 1234 files indexed

::diagnostics
→ (full diagnostic report with detailed health checks)
```
//BOUNDARY: Full diagnostics may take several seconds. Fast mode skips expensive checks.

**Depth**
- `diagnostics.fast`: connectivity, pipeline status, basic counts
- `diagnostics`: all fast checks plus embedding health, search validation, detailed metrics
- Useful after `::host.restart` to verify the new host is healthy

---

## Help

```
::diagnostics --help
→ ::diagnostics — Run full system health diagnostics
  Usage: ::diagnostics

::diagnostics.fast --help
→ ::diagnostics.fast — Run quick system health checks
  Usage: ::diagnostics.fast
```
