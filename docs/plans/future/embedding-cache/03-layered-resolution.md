# Plan: Layered Cache Resolution

Implements: [Embedding Cache Design](../../../designs/future/embedding-cache.md) — multi-path support, shared/read-only caches

## Scope

**Covers:**
- Multi-path cache resolution — check paths in priority order
- Read-only shared cache support — read from paths the host doesn't own
- Write-through to local — cache hits from shared layers written to local cache
- `Paths` list configuration (first path is write target, rest are read-only)
- Path validation and error handling for unreachable paths
- Tests for layered lookup behavior
- `help://` documentation update for shared cache setup

**Does not cover:**
- Core cache read/write (Plan: 01-local-cache — prerequisite)
- Compaction of shared caches (the shared cache owner's responsibility)
- Cloud/HTTP cache sources (future consideration)
- Cache population tooling for shared caches (follow-on work)

## Enables

Once layered resolution exists:
- **Team-wide cache sharing** — one developer imports and embeds; everyone else hits the shared cache
- **New developer onboarding** — point at shared cache path, get instant semantic search on team repos
- **Organizational cache** — IT publishes embeddings for standard libraries and frameworks
- **Future: cloud cache** — same layered architecture, HTTP-backed reader as another layer

## Prerequisites

- Plan 01 (Local Embedding Cache) completed — `EmbeddingCache` class exists with single-path support
- Plan 02 (Cache Maintenance) recommended but not required — shared caches may need independent compaction

## North Star

A developer adds one path to their config and immediately benefits from every embedding their team has ever computed. No coordination, no synchronization, no special tooling — just a directory full of parquet files.

## Done Criteria

### Layered Lookup

- The EmbeddingCache shall check paths in the order specified in `Paths` configuration
- When a hash is found in an earlier path, later paths shall not be checked for that hash
- When a hash is found in a shared (non-first) path, the embedding shall be written through to the local (first) path
  - Write-through failure shall not affect the lookup result
- The EmbeddingCache shall combine results across paths — a batch lookup may have hits from different paths

### Read-Only Shared Paths

- The EmbeddingCache shall only write new embeddings to the first path in the `Paths` list
- The EmbeddingCache shall read from all paths in the `Paths` list
- When a shared path requires no write access, the EmbeddingCache shall function with read-only filesystem permissions on that path

### Path Validation

- When a path in `Paths` does not exist and it is the first (local) path, it shall be created
- When a path in `Paths` does not exist and it is not the first path, it shall be skipped with a debug log
- When a path in `Paths` is unreachable (network error, permissions), it shall be skipped for that lookup with a debug log
  - The same path shall be retried on subsequent lookups (transient failures recover)
- When `Paths` is empty or null, the cache shall use the default local path

### Configuration

- The `Paths` property shall be a list of strings in `EmbeddingCacheSettings`
- The first path in the list shall be the write target
- The default shall be `["~/.repoql/embedding-cache/"]` (single local path, identical to Plan 01 behavior)
- Paths shall support `~` expansion and environment variables

### Documentation

- The `help://` docs shall explain how to set up a shared cache
  - How to populate it (copy local cache to shared location)
  - How to configure clients to read from it
  - Permissions required (read-only on shared, read-write on local)

## Constraints

- **First path is always the write target** — no configuration for "which path to write to" beyond ordering (design: local write-only)
- **No distributed locking** — shared paths are read-only from the client's perspective. The owner manages their own compaction (design: no coordination)
- **Network path latency is the caller's problem** — if a shared path is slow, it slows lookups. Consider path ordering. Future: async/timeout on shared paths
- **No authentication** — paths are filesystem paths (local, network share, mounted drive). Authenticated sources are future work

## References

- [Embedding Cache Design](../../../designs/future/embedding-cache.md) — layered resolution section, configuration schema
- [Embedding Cache Flow](../../../flows/future/embedding-cache.md) — layered resolution flow
- [Plan: 01-local-cache](01-local-cache.md) — prerequisite, establishes single-path behavior
- [Plan: 02-cache-maintenance](02-cache-maintenance.md) — compaction applies to local path; shared paths managed independently
- [Testing guidelines](../../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions

## Error Policy

Shared path failures shall never affect local cache or embedding operations. When a shared path fails:
1. Log at debug level (shared paths are optional — absence is not a warning)
2. Skip the path for this lookup
3. Continue checking remaining paths
4. Fall through to compute if no path has the entry

A misconfigured shared path degrades to "local cache only" — the behavior from Plan 01. No regression, no error escalation.
