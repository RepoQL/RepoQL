# Discovery Notes — Diagnose Skill

## Sources

Comprehensive codebase investigation of all diagnostic and troubleshooting functionality.

### Client-side diagnostics
- `DiagnosticsCollector.cs` — 12 probes, best-effort, never throws. Two modes: Fast (connectivity only) and Full (everything including DB queries).
- `SelfTestRunner.cs` — Entry point for diagnostic collection.
- `DiagnosticsCommand.cs` — Four commands: `diagnostics`, `diagnostics.fast`, `diagnostics.index`, `diagnostics.cloud`. Each has distinct scope.
- `ErrorClassifier.cs` — Classifies exceptions as infrastructure vs user. Infrastructure triggers auto-diagnostics. User errors get SQL enrichment (table name extraction, DESCRIBE hints, help:// links).

### Host-side health
- `HealthDiagnosticsInterceptor.cs` — gRPC interceptor adding trailers to health checks: degraded services, RPC activity, hang detection.
- `ServiceDegradationTracker.cs` — Sticky degradation for 5 service kinds (embeddings, mcp, mounts, indexer, watcher). Persists to `services-start.json`.
- `RpcActivityTracker.cs` — Lock-free tracking of active gRPC calls. Hang threshold 30s (configurable). Excludes health/lease/watch calls.

### SQL diagnostic surface
- `DiagnosticsUdf.cs` — `indexing_diagnostics()`, `indexing_queue()`
- `QueueObservabilityUdf.cs` — `processing_queue()` (table), `system_health()` (single row)
- `CloudDiagnosticsUdf.cs` — `cloud_diagnostics()` (auth, inference, embedding)
- `UriRegistryUdf.cs` — `_indexer_status_internal()`, `_registry_summary_internal()`, `failed_files()`, `_scope_readiness_internal()`

### Recovery mechanisms
- `QueueCommand.cs` — `queue.cancel`, `queue.skip`, `queue.retry` for surgical file-level intervention.
- `NoMatchDiagnostics.cs` — Context-aware error messages when reads/searches fail. Distinguishes symbol-not-found from file-not-found, checks for pending files.
- Auto-relaunch of host on crash. Host lifecycle commands: `host.restart`, `host.stop`.

### Persistent artifacts
- `.repoql/diagnostics/` — JSON reports: `socket-bind.json`, `existing-host.json`, `database-init.json`, `services-start.json`
- `.repoql/host.log` — Rolling file log
- `.repoql/host-stderr.log` — Stderr capture

### Documented failure modes
- 9 MCP failure modes in `docs/flows/current/mcp/failure-modes/`
- 8 host startup failure modes in `docs/flows/current/host/failure-modes/`

## Key Insight

The diagnostic surface is layered: connection → host → database → indexing → services. This maps directly to the escalation path. The existing thin `commands/diagnose.md` in the plugin misses 90% of the surface area and uses outdated syntax.

## What Claude Gets Wrong Without This Skill

1. Doesn't know diagnostic commands exist — retries blindly or asks user to restart
2. Doesn't know the escalation order — jumps to expensive diagnostics or nuclear restart
3. Can't interpret diagnostic output — degraded services, RPC hanging, etc. are opaque
4. Doesn't know SQL diagnostic functions exist
5. Doesn't know queue control commands for surgical intervention
6. Doesn't understand auto-diagnostics (error classification triggers them automatically)
