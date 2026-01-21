# Host Failure Modes

How the host detects and handles failure conditions during startup and operation.

Based on research: `docs/research/failure-modes/host-startup-failure-modes.md`

## Startup Phases

```
Launch → Preflight → Shutdown Existing → Socket Bind → DB Init → Services Start → Ready
```

Each phase can fail. The goal: detect early, surface clearly, recover where possible.

## Failure Modes by Phase

| Phase | Failures | Document |
|-------|----------|----------|
| Preflight | S1-S5: Path validation, repo detection, directory creation | `preflight.md` |
| Shutdown Existing | S6-S9: AF_UNIX check, shutdown RPC, kill, DB lock | `existing-host.md` |
| Socket Bind | S10-S16: Stale socket, path length, platform support | `socket-binding.md` |
| Database Init | S18-S20: Lock, corruption, temp directory | `database-init.md` |
| Services Start | S21-S25: Embeddings, MCP, mounts, indexing, watcher | `services-start.md` |
| Readiness | S26-S28: Premature health, idle shutdown race, hangs | `readiness.md` |
| Configuration | S17, S19: Malformed config, invalid env vars | `configuration.md` |

## Infrastructure

| Topic | Document |
|-------|----------|
| Logging | `host-logging.md` - Rolling file logs in .repoql/, exit records |

## Diagnostic Data Model

Host startup collects facts into structured data:

```
HostStartupReport
├── Phase: Preflight | ShutdownExisting | SocketBind | DbInit | ServicesStart | Ready
├── StartedAt: DateTime
├── DurationMs: int
│
├── Preflight
│   ├── RequestedPath: string?
│   ├── ResolvedRepoRoot: string
│   ├── RepoMarkerFound: bool
│   ├── RepoqlDirCreated: bool
│   └── Errors: string[]
│
├── ExistingHost
│   ├── SocketExisted: bool
│   ├── ShutdownAttempted: bool
│   ├── ShutdownSucceeded: bool
│   ├── ProcessKilled: bool
│   ├── PreviousPid: int?
│   └── Errors: string[]
│
├── Socket
│   ├── Path: string
│   ├── PathLength: int
│   ├── Platform: string
│   ├── BoundSuccessfully: bool
│   └── Errors: string[]
│
├── Database
│   ├── Path: string
│   ├── Existed: bool
│   ├── OpenedSuccessfully: bool
│   ├── WasLocked: bool
│   ├── LockHolder: ProcessInfo?
│   └── Errors: string[]
│
├── Services
│   ├── EmbeddingsInitialized: bool
│   ├── McpInitialized: bool
│   ├── MountsRestored: bool
│   ├── IndexerStarted: bool
│   ├── WatcherStarted: bool
│   └── Errors: string[]
│
└── Readiness
    ├── HealthStatus: Serving | NotServing
    ├── IndexerReady: bool
    ├── InitialScanComplete: bool
    └── Errors: string[]
```

## Error Reporting Principle

Surface facts, not guesses:

```
❌ Socket bind failed
   Path: /very/long/path/that/exceeds/limit/.repoql/repoql.sock
   Length: 112 characters
   Platform limit: 108 (Unix) / 104 (macOS)

   Shorten the path or set REPOQL_SOCKET to an alternate location.
```

Not:
```
❌ Something went wrong with the socket
```

## Related

- Client failure modes: `docs/flows/future/mcp/failure-modes/`
- Research: `docs/research/failure-modes/host-startup-failure-modes.md`
- North star: `docs/north-star/reliability.md`
