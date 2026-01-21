---
description: Host startup failure modes for repoql serve and resilience gaps
tags: [failure-modes, serve, host, startup, resilience]
audience: { human: 60, agent: 40 }
purpose: { research: 70, reference: 30 }
---

# RepoQL Host Startup Failure Modes

Scope: `repoql serve` startup from process launch through Kestrel binding and initial hosted service start.

## Hit List (resilience holes)

| ID | Phase | Failure mode or trigger | Symptom | Resilience hole |
| --- | --- | --- | --- | --- |
| S1 | Preflight | Invalid `repository` path or `Path.GetFullPath` failure | Serve exits with unhandled exception | No validation or friendly error message |
| S2 | Preflight | No `.git` or `.repoql` marker and fallback to drive root | Host indexes wrong repository silently | No warning when fallback root is used |
| S3 | Preflight | `.repoql` exists as file and move fails | Exception during startup | No recovery path or alternate location |
| S4 | Preflight | `.repoql` directory cannot be created (permissions, read-only, long path) | Exception before host build | No preflight check or remediation guidance |
| S5 | Preflight | `REPOQL_SOCKET` or `.repoql/socket.path` contains invalid or too-long path | Later socket bind or connect failure | No validation of overrides or mapping file content |
| S6 | Shutdown | AF_UNIX unsupported during shutdown probe | Unhandled exception before startup | No PlatformNotSupported handling or fallback |
| S7 | Shutdown | Shutdown RPC fails with status other than Unavailable | Serve exits without cleanup | No retry or stale socket fallback |
| S8 | Shutdown | Existing host refuses to exit and cannot be killed | DB remains locked; startup times out | No escalation or diagnostics for kill failure |
| S9 | Shutdown | DB locked by other process or zombie host | `TimeoutException` after 45s wait | No lock owner detection or guidance |
| S10 | Socket cleanup | Stale socket cannot be removed (rename/delete fails or races) | Bind fails with address in use | No retry strategy or alternate socket path |
| S11 | Socket bind | Socket path length >= 108 chars | `ArgumentException` from transport | No automatic shortening or path rewrite |
| S12 | Socket bind | macOS path length 104-107 chars | Bind fails with ENAMETOOLONG | Length guard is 108, not macOS 104 |
| S13 | Socket bind | Windows AF_UNIX missing or disabled | Bind fails; host exits | No fallback transport or explicit check |
| S14 | Socket bind | WSL2 DrvFS socket path (e.g., `/mnt/c`) | ENOTSUP or bind failure | No detection or forced LxFS path |
| S15 | Socket bind | Permission or policy blocks socket creation (SELinux, AppArmor, sandbox) | Bind fails or clients cannot connect | No permission probe or recovery hints |
| S16 | Socket bind | Windows backslash paths or WSL colon paths | Inconsistent bind/connect behavior | No path normalization or sanitation |
| S17 | Config | Malformed `appsettings.<env>.json` | Startup exception during config load | No validation or error context surfaced |
| S18 | DuckDB | Database open fails (lock, corruption, permissions) | Host exits on startup | No auto-recovery or reindex fallback |
| S19 | DuckDB | Invalid `DUCKDB_*` env values | `SET` statement failure at init | No validation or safe defaults |
| S20 | DuckDB | Temp directory creation fails | Startup exception | No fallback temp path |
| S21 | Embeddings | `REPOQL_EMBED_MODEL_PATH` set but ONNX init fails | `InvalidOperationException` aborts startup | No fallback to hashed provider when explicit path fails |
| S22 | MCP | Invalid MCP config or global config parse failure | DI build or host start fails | No isolation to start host without MCP |
| S23 | Mount restore | `_db.GetAllMounts()` throws | Hosted service start fails; host exits | No catch or delayed retry |
| S24 | Indexing scan | Full scan enumeration throws (IO/permission) | Background service fails; host stops | No continue-on-error or partial scan strategy |
| S25 | Watcher start | Watcher init fails (inotify limits, OS errors) | Background service fails; host stops | No fallback to polling-only mode |
| S26 | Readiness | Health set to Serving before indexer readiness | Client sees healthy host with broken indexing | No readiness gate tied to initial scan success |
| S27 | Implicit start | IdleShutdown timer fires before lease established | Host exits during startup | No startup grace period or lease hold |
| S28 | Startup hang | Long DB init or scan delays | Client times out; host appears hung | No explicit startup progress or phase reporting |

## Relevant code paths

- `src/RepoQL.ConsoleApp/Commands/ServeCommands.cs`
- `src/RepoQL.ConsoleApp/Host/GrpcServerHelper.cs`
- `src/RepoQL.Protocol/Transport/UnixSocketTransport.cs`
- `src/RepoQL.Contracts/RepoLocator.cs`
- `src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs`
- `src/RepoQL.Data.DuckDB/DuckDbDataStore.cs`
- `src/RepoQL.ConsoleApp/Host/MountRestorationService.cs`
- `src/Indexing/RepoQL.Indexing/Hosting/RepoqlHost.cs`
- `src/RepoQL.ConsoleApp/Host/IdleShutdownHostedService.cs`

## Research inputs

- `docs/research/repoql/client-server-failure-modes.md`
- `docs/research/failure-modes/unix-domain-socket-failure-modes.md`
- `docs/research/failure-modes/grpc-connection-lifecycle-failure-modes.md`
- `docs/research/failure-modes/cross-platform-process-management.md`
