---
description: Plan for enhanced diagnostic problem rules — socket bind errors, log extraction, disk space, directory health
tags: [diagnostics, problem-rules, probes, offline, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Enhanced Problem Rules

Implements: [Local-First Recovery Design](../../../designs/future/local-first-recovery.md) — Enhanced Problem Rules, new probes

## Scope

**Covers:**
- Socket bind error rule in `DiagnosticReportProblems.Build()`
- Host log error extraction — enhance existing "Previous host crashed" rule to include the actual error line
- Disk space probe in `DiagnosticsCollector` + "Low disk space" rule
- `.repoql/` directory health probe in `DiagnosticsCollector` + "No .repoql directory" rule
- New fields on `DiagnosticReport` for disk space and directory health data
- Tests for each new rule and probe

**Does not cover:**
- Version mismatch detection (Plan: [02-cross-session-state](02-cross-session-state.md) — requires host.version file)
- Cross-session stderr reading (Plan: [02-cross-session-state](02-cross-session-state.md))
- Restart command changes (Plan: [03-reliable-restart](03-reliable-restart.md))

## Enables

- Offline diagnosis covers the most common environmental failures (disk full, permissions, missing directory) instead of just host/socket state
- Agent sees the actual crash error in the problem, not "inspect host log" — eliminates one investigation step
- Plan 03 can rely on richer diagnostic reports when deciding restart strategy

## Prerequisites

None. All required infrastructure exists:
- `DiagnosticReportProblems.Build()` in `src/RepoQL.Protocol/Diagnostics/DiagnosticReport.cs` — 7 existing rules to extend
- `DiagnosticsCollector.CollectAsync()` in `src/RepoQL.ConsoleApp/Diagnostics/DiagnosticsCollector.cs` — best-effort probe pattern established
- `SocketBindError` field already populated from `socket-bind.json` artifact
- Host log tail already collected as `HostLogTail` on `DiagnosticReport`

## North Star

Every offline problem the agent encounters produces a specific diagnosis with actionable guidance. The agent never sees "inspect host log" — it sees the crash reason. The agent never sees "host won't start" — it sees "disk full" or "permissions error" or "no .repoql directory."

## Done Criteria

### Socket Bind Error Rule

- `DiagnosticReportProblems.Build()` shall check `SocketBindSucceeded == false` and produce a problem titled "Socket bind failed"
- The problem shall include `SocketBindError` in its facts
- The problem guidance shall say "Check permissions on the socket directory: {SocketBindError}"
- A test shall verify the rule fires when `SocketBindSucceeded = false` and `SocketBindError` is populated
- A test shall verify the rule does not fire when `SocketBindSucceeded` is null (bind report not written yet)

### Host Log Error Extraction

- The existing "Previous host crashed" rule shall include the actual error line from `HostLogTail` in its facts, not just `host_log=error`
- When `HostRunning == false` and `HostLogTail` contains a line matching `\bERR(OR)?\b` (case-insensitive), the last such line shall be extracted as a `crash_reason` fact
- When multiple ERROR lines exist, use the last one (closest to crash)
- When no ERROR line is found but the host is not running, the rule shall still fire with existing behavior (no regression)
- A test shall verify the crash reason appears in problem facts when an ERROR line exists in the log tail
- A test shall verify the rule degrades gracefully when the log tail has no ERROR lines

### Disk Space Probe

- `DiagnosticsCollector` shall probe free disk space on the volume containing `.repoql/`
- The probe shall set `DiskFreeMb` (nullable int) on `DiagnosticReport`
  - If the probe fails (path doesn't exist, permissions), record the failure in `ProbeFailures` and leave `DiskFreeMb` null
- `DiagnosticReportProblems.Build()` shall check `DiskFreeMb < 100` and produce a problem titled "Low disk space"
- The problem guidance shall say "Free disk space on the volume containing .repoql/ ({DiskFreeMb} MB remaining)"
- The threshold (100 MB) shall be a constant, not configurable
- A test shall verify the rule fires when disk space is below threshold
- A test shall verify the rule does not fire when `DiskFreeMb` is null (probe failed)

### Directory Health Probe

- `DiagnosticsCollector` shall probe whether the `.repoql/` directory exists when `RepoRoot` is known
- The probe shall set `RepoQlDirectoryExists` (nullable bool) on `DiagnosticReport`
  - If the probe fails, record in `ProbeFailures` and leave null
- `DiagnosticReportProblems.Build()` shall check `RepoQlDirectoryExists == false` and produce a problem titled "No .repoql directory"
- The problem guidance shall say "Run a RepoQL command to initialize the repository"
- The rule shall not fire when `RepoRoot` is null (no repo detected — different problem)
- A test shall verify the rule fires when directory does not exist
- A test shall verify the rule does not fire when `RepoRoot` is null

## Constraints

- **Best-effort probes** — disk space and directory health probes follow the existing pattern: catch exceptions, record in `ProbeFailures`, continue. Design: "individual probe failures are recorded but never stop the overall flow."
- **No new persistent state** — probes read existing file system state. Design constraint.
- **Cross-platform** — disk space probe must work on both Windows (`DriveInfo`) and Unix. Use `DriveInfo` for the `.repoql/` volume path — works cross-platform in .NET.
- **Threshold not configurable** — 100 MB is reasonable for all environments. Adding configuration for an edge-case probe violates the north star ("must not require configuration").

## References

- [Local-First Recovery Design](../../../designs/future/local-first-recovery.md) — Enhanced Problem Rules section
- [Offline Diagnostics Flow](../../../flows/future/diagnostics/offline-diagnostics.md) — gaps table identifies these rules
- `src/RepoQL.Protocol/Diagnostics/DiagnosticReport.cs` — `DiagnosticReportProblems.Build()` and `DiagnosticReport` record
- `src/RepoQL.ConsoleApp/Diagnostics/DiagnosticsCollector.cs` — probe infrastructure
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions

## Error Policy

Probe failures are never fatal. When a probe throws:
1. Catch the exception
2. Record it in `DiagnosticReport.ProbeFailures` with the probe name and exception message
3. Leave the corresponding report field as null
4. Continue to the next probe

This is the established pattern in `DiagnosticsCollector` — the design explicitly requires it.
