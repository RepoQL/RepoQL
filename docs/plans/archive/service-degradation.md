# Plan: Service Degradation

Implements: [Reliability Design](../designs/reliability.md) — Host Startup section

## Scope

**Covers:**
- Graceful degradation for non-critical services
- Embeddings service failure handling
- MCP integration failure handling
- Mount restoration failure handling
- Indexer startup failure handling
- File watcher failure handling
- Degraded state surfacing in health check

**Does not cover:**
- Database init (Plan: Database Init) — database is critical, not degradable
- Socket binding (Plan: Host Takeover) — socket is critical
- Health check protocol (Plan: Health Progress)

## Enables

Once Service Degradation exists:
- **Partial functionality over total failure** — query works even if embeddings failed
- **Clear degradation warnings** — user knows what's limited
- **Faster startup** — non-critical failures don't block SERVING status
- **Better reliability** — one failing component doesn't bring down the host

## Prerequisites

- Database opened successfully (Plan: Database Init)
- Socket bound and listening (Plan: Host Takeover)

## North Star

When embeddings fail to initialize, the host is still useful — queries work, just without semantic search. The user sees a clear warning, not a crash.

## Done Criteria

### Service Classification

- The host shall classify services as critical or degradable:
  - **Critical**: Database, Socket, gRPC server — failure = exit
  - **Degradable**: Embeddings, MCP, Mounts, Indexer, Watcher — failure = warn and continue

### Embeddings Degradation

- When ONNX model fails to load, log warning with error details
- When embeddings service fails, fall back to hash-based embeddings
- Set degradation flag: `EmbeddingsDegraded = true`
- Continue startup with degraded embeddings

### MCP Integration Degradation

- When MCP tool registration fails, log warning
- Disable MCP-specific features
- Set degradation flag: `McpDegraded = true`
- Continue startup without MCP

### Mount Restoration Degradation

- When restoring imported mounts fails, log warning per mount
- Skip failed mounts, continue with successful ones
- Set degradation flag: `MountsDegraded = true` if any failed
- Report which mounts failed in health check

### Indexer Degradation

- When indexer fails to start (not stall — actual startup failure), log warning
- Set degradation flag: `IndexerDegraded = true`
- Continue startup — existing index still queryable
- Report indexer status in health check

### Watcher Degradation

- When file watcher fails to start, log warning
- Set degradation flag: `WatcherDegraded = true`
- Continue startup — manual reindex still works
- Report watcher status in health check

### Health Check Integration

- Each degradable service shall have its own health registration:
  - `repoql.embeddings`, `repoql.indexer`, `repoql.watcher`, `repoql.mcp`
- When service degrades, call `health.SetStatus("repoql.{service}", NOT_SERVING)`
- Overall health ("") remains SERVING even when individual services degraded
- Clients watching specific services get notified immediately on degradation

### Degradation Surfacing

- On first query after degradation, warn about limited functionality
- Include degradation info in `:diagnostics:` output
- Log degradation state at INFO level on startup completion

## Constraints

- **Critical services still fatal** — database/socket failures exit immediately
- **Warn once, not repeatedly** — don't spam logs with degradation warnings
- **Degradation is sticky** — don't try to recover degraded services at runtime

## References

- [Reliability Design](../designs/reliability.md) — graceful degradation decision
- [Services Start Flow](../flows/future/host/failure-modes/services-start.md) — detailed scenarios

## Error Policy

For degradable services:
1. Catch initialization exception
2. Log warning with error details
3. Set degradation flag
4. Continue startup
5. Report degradation in health check
