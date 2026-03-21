# Plans

Implementation plans for the [Reliability Design](../designs/reliability.md).

## Plans

| Plan | Scope | Dependencies |
|------|-------|--------------|
| [Host Persistence](host-persistence.md) | File logging, exit records | None |
| [Preflight Validation](preflight-validation.md) | Path/config validation before startup | None |
| [Host Takeover](host-takeover.md) | Shutdown existing host, socket binding | Preflight Validation |
| [Database Init](database-init.md) | Open DB, lock handling, corruption recovery | Host Takeover |
| [Service Degradation](service-degradation.md) | Graceful degradation for non-critical services | Database Init |
| [Health Progress](health-progress.md) | Startup phase/progress in health check | Service Degradation |
| [Connection Recovery](connection-recovery.md) | Channel state, lease handling, circuit breaker | None |
| [Diagnostics](diagnostics.md) | Fact collection, problem identification | Host Persistence, Health Progress |

## Order

```
                                    ┌─► Connection Recovery (independent)
                                    │
Host Persistence ───────────────────┼─────────────────────────┐
                                    │                         │
Preflight Validation ───┐           │                         ▼
                        ▼           │                    Diagnostics
                  Host Takeover     │                         ▲
                        │           │                         │
                        ▼           │                         │
                  Database Init     │                         │
                        │           │                         │
                        ▼           │                         │
               Service Degradation  │                         │
                        │           │                         │
                        ▼           │                         │
                  Health Progress ──┴─────────────────────────┘
```

**Phase 1 (no dependencies):**
- Host Persistence
- Preflight Validation
- Connection Recovery

**Phase 2 (depends on Phase 1):**
- Host Takeover (needs Preflight Validation)

**Phase 3 (depends on Phase 2):**
- Database Init (needs Host Takeover)

**Phase 4 (depends on Phase 3):**
- Service Degradation (needs Database Init)

**Phase 5 (depends on Phase 4):**
- Health Progress (needs Service Degradation)

**Phase 6 (depends on Phase 1 + Phase 5):**
- Diagnostics (needs Host Persistence + Health Progress)

## Coverage

### Host Failure Modes

| Flow Document | Plan | Coverage |
|---------------|------|----------|
| preflight.md | Preflight Validation | Path validation, repo detection |
| existing-host.md | Host Takeover | Shutdown RPC, kill escalation, PID file |
| socket-binding.md | Host Takeover, Preflight Validation | Stale cleanup, path length, WSL, permissions, normalization |
| database-init.md | Database Init | Lock classification, corruption recovery, temp-dir, schema |
| services-start.md | Service Degradation | Embeddings, MCP, mounts, indexer, watcher degradation |
| readiness.md | Health Progress | Startup grace period, progress reporting, stall detection |
| configuration.md | Preflight Validation | JSON parse errors, REPOQL_*/DUCKDB_* validation |
| host-logging.md | Host Persistence | File sink, crash logging |

### MCP Failure Modes

| Flow Document | Plan | Coverage |
|---------------|------|----------|
| channel-stuck.md | Connection Recovery | State check, auto-reconnect |
| lease-expired.md | Connection Recovery, Diagnostics | Heartbeat handling, lease state probe |
| host-crashed.md | Connection Recovery, Diagnostics | Circuit breaker, log-based crash detection |
| host-unhealthy.md | Diagnostics, Health Progress | Per-service health, phase reporting |
| database-locked.md | Diagnostics, Database Init | Lock holder identification, zombie kill |
| index-incomplete.md | Diagnostics, Health Progress | Progress/stall probes, phase reporting |
| diagnostics.md | Diagnostics | Full fact collection, problem identification |
| wrong-working-directory.md | — | ✓ Already mitigated (REPOQL_CWD, primary://) |
| wsl-socket-path.md | — | ✓ Already mitigated (REPOQL_SOCKET) |

## Lifecycle

Plans are deleted when implemented. The design and code are the durable artifacts.



##  Recommended Order                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             

  1. Host Persistence — Quick win. Fixes scary disconnect logging, adds file sink. Foundation for diagnostics.                                                                                                                                                                                                                                                                      
  2. Connection Recovery — Fixes current stuck channel pain. Auto-relaunch on crash. Circuit breaker.                                                                                                                                                                                                                                                                               
  3. Preflight Validation — Catches problems early with clear errors. Config/env validation.                                                                                                                                                                                                                                                                                        
  4. Host Takeover — Clean handoff between hosts. Stale socket cleanup. Depends on preflight.                                                                                                                                                                                                                                                                                       
  5. Database Init — Lock handling, corruption recovery, schema recreation. Depends on takeover.                                                                                                                                                                                                                                                                                    
  6. Service Degradation — Graceful degradation for non-critical services. Depends on DB init.                                                                                                                                                                                                                                                                                      
  7. Health Progress — Watch + Check, per-service health, progress reporting. Depends on degradation.                                                                                                                                                                                                                                                                               
    8. Diagnostics — Ties everything together. Needs host persistence + health progress to have data to report.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    

###   Rationale                                                                                                                                                                                                                                                                                                                                                                       

  - Front-loads quick wins (1-2 fix current pain)                                                                                                                                                                                                                                                                                                                                   
  - Respects technical dependencies                                                                                                                                                                                                                                                                                                                                                 
  - Each step delivers standalone value                                                                                                                                                                                                                                                                                                                                             
  - Diagnostics last because it consumes what others produce   
