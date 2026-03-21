---
description: Plan for cross-session host state — stderr to file, version file, version mismatch detection
tags: [diagnostics, cross-session, stderr, version, host, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Cross-Session Host State

Implements: [Local-First Recovery Design](../../../designs/future/local-first-recovery.md) — Cross-Session Host State, Version Mismatch rule

## Scope

**Covers:**
- Host writes stderr to `.repoql/host.stderr.log` — ring-buffer, overwritten on restart
- Host writes version to `.repoql/host.version` on startup — before socket bind
- `DiagnosticsCollector` reads `host.stderr.log` as a local probe when the in-memory stderr cache is empty
- `DiagnosticsCollector` reads `host.version` for offline version comparison
- New fields on `DiagnosticReport`: `HostStderrFromFile` (string), `HostVersionFile` (string)
- Version mismatch problem rule in `DiagnosticReportProblems.Build()`
- Tests for host-side file writes and collector-side probes

**Does not cover:**
- Enhanced problem rules for disk space, directory health, etc. (Plan: [01-problem-rules](01-problem-rules.md))
- Restart command changes (Plan: [03-reliable-restart](03-reliable-restart.md))

## Enables

- Any MCP client session can see crash stderr, not just the session that launched the host — eliminates the most common blind spot in crash diagnosis
- Version mismatch detected offline before it causes confusing protocol errors
- Plan 03 can read cross-session host state during restart to make better decisions

## Prerequisites

None. Both changes are independent additions:
- Host process wrapper in `src/RepoQL.ConsoleApp/Commands/ServeCommands.cs` — where stderr capture would be added
- `DiagnosticsCollector` in `src/RepoQL.ConsoleApp/Diagnostics/DiagnosticsCollector.cs` — where new probes would be added
- `DiagnosticReport` in `src/RepoQL.Protocol/Diagnostics/DiagnosticReport.cs` — where new fields and rule would live

## North Star

Crash information is never lost because the wrong client session started the host. An agent in any session sees the same crash details — stderr, exit code, version. The diagnostic report is equally useful whether you launched the host or connected to one already running.

## Done Criteria

### Host Stderr to File

- The host process shall write stderr output to `.repoql/host.stderr.log` in addition to any in-memory capture
- The file shall be created (or truncated) when the host starts — not appended across restarts
- The file shall contain the last N lines of stderr (N = 200, matching the in-memory buffer size)
  - If stderr exceeds N lines during the host's lifetime, older lines shall be dropped (ring-buffer behavior)
- The host shall write to the file incrementally as stderr lines arrive, not only on shutdown
  - If the host crashes, lines written before the crash are preserved
- When the host exits normally, the file shall remain for post-mortem inspection
- A test shall verify that stderr content is written to the file
- A test shall verify that the file is truncated on restart (not appended)

### Host Version File

- The host shall write its version (from assembly metadata) to `.repoql/host.version` as a single line
- The write shall occur early in startup, before socket bind — design requirement: "even a crash during startup leaves a version file for offline detection"
- The file shall contain only the version string (e.g., `1.4.1`), no other formatting
- The version string shall come from the same source as the existing version reporting in the host
- A test shall verify the file is written on startup

### Stderr File Probe

- `DiagnosticsCollector` shall read `.repoql/host.stderr.log` as a local probe
- The probe shall only read the file when the in-memory stderr cache from `GetHostDiagnostics()` is empty (different session launched the host, or host was started via CLI)
- When both sources are available, prefer the in-memory cache (fresher, guaranteed complete for current session)
- The probe shall set `HostStderrFromFile` on `DiagnosticReport` with the file contents (last 50 lines, matching existing stderr tail behavior)
- `DiagnosticReport.ToString()` shall include `HostStderrFromFile` in the "host stderr" section when the in-memory stderr is empty
- The probe shall follow best-effort pattern: file missing or unreadable → record in ProbeFailures, continue
- A test shall verify the probe reads the file when in-memory cache is empty
- A test shall verify the probe is skipped when in-memory cache has content

### Version Mismatch Rule

- `DiagnosticsCollector` shall read `.repoql/host.version` and set `HostVersionFile` on `DiagnosticReport`
- `DiagnosticReportProblems.Build()` shall compare `HostVersionFile` against the client's own version
- When versions differ, produce a problem titled "Version mismatch"
- The problem facts shall include `client_version` and `host_version`
- The problem guidance shall say "Client v{x}, host was v{y}. Restart may resolve."
- The rule shall not fire when `HostVersionFile` is null (file doesn't exist — host never started or pre-dates this feature)
- A test shall verify the rule fires when versions differ
- A test shall verify the rule does not fire when versions match
- A test shall verify the rule does not fire when `HostVersionFile` is null

## Constraints

- **Ring-buffer, not append** — stderr file is truncated on restart. Design chose this over append to prevent unbounded growth. Same approach as `host.log`.
- **In-memory cache preferred** — the file is a fallback for cross-session scenarios. When the current session launched the host, the in-memory cache is authoritative.
- **Version before socket bind** — the version file must be written before socket bind so that even a startup crash leaves a version breadcrumb. Design constraint.
- **No new persistent state beyond two files** — `host.stderr.log` and `host.version` are the only additions. Design constraint: "No new databases, no new services."

## References

- [Local-First Recovery Design](../../../designs/future/local-first-recovery.md) — Cross-Session Host State section, Version Mismatch rule
- [Offline Diagnostics Flow](../../../flows/future/diagnostics/offline-diagnostics.md) — gaps: "Stderr not always captured", "No version mismatch detection offline"
- `src/RepoQL.ConsoleApp/Commands/ServeCommands.cs` — host startup, where file writes belong
- `src/RepoQL.ConsoleApp/Diagnostics/DiagnosticsCollector.cs` — where probes belong
- `src/RepoQL.Protocol/Diagnostics/DiagnosticReport.cs` — where fields and rule belong
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions

## Error Policy

Host-side file writes (stderr, version) should be best-effort during startup. If `.repoql/` is unwritable, log a warning and continue — the host should still start. Missing files on the collector side are handled by the standard probe failure pattern (record in ProbeFailures, leave field null).
