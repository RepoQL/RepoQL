# Plan: Cache Maintenance

Implements: [Embedding Cache Design](../../../designs/future/embedding-cache.md) — compaction, eviction, lockfile management

## Scope

**Covers:**
- Compaction — merge multiple parquet files into one, deduplicate by hash
- Eviction — drop oldest entries when cache exceeds size limit
- Stale lockfile recovery — detect dead PID, reclaim
- Trigger logic — on host startup and when file count exceeds threshold
- `MaxSizeMb` and `CompactionThreshold` configuration
- Tests for all components

**Does not cover:**
- Core cache read/write (Plan: 01-local-cache — prerequisite)
- Multi-path resolution (Plan: 03-layered-resolution)
- Scheduled/periodic compaction (follow-on if needed)

## Enables

Once maintenance exists:
- **Cache doesn't grow unbounded** — eviction enforces the configured size limit
- **Read performance stays stable** — compaction reduces file count, fewer parquet files to glob
- **Concurrent hosts don't deadlock** — lockfile with PID detection prevents stale locks

Without this plan, the local cache from Plan 01 works but accumulates files indefinitely. Acceptable for days or weeks, problematic over months.

## Prerequisites

- Plan 01 (Local Embedding Cache) completed — `EmbeddingCache` class exists with read/write capability
- Cache parquet schema established and stable

## North Star

The cache maintains itself. No manual cleanup, no growing disk usage, no stale lockfiles blocking compaction forever. An agent never encounters a cache-related problem that requires human intervention.

## Done Criteria

### Compaction

- When file count in the cache directory exceeds `CompactionThreshold`, the EmbeddingCache shall attempt compaction
- The compaction shall read all parquet files, deduplicate by `text_hash` (keep newest by `created_at`), and write a single output file
- The compaction shall delete original files only after the output file is successfully written
- If the output file write fails, the original files shall be preserved unchanged
- The compaction shall be atomic from the reader's perspective — write the merged output file via temp-file-then-rename before deleting originals
  - On Windows, files with open reader handles cannot be deleted; compaction shall skip those files and retry next cycle
  - On Linux, open handles survive deletion; readers complete normally

### Eviction

- While the total on-disk cache size exceeds `MaxSizeMb` during compaction, the compaction shall drop entries with the oldest `created_at` first until under the limit (oldest-first eviction, not true LRU — parquet files are immutable so access times cannot be updated)
- When `MaxSizeMb` is 0, eviction shall be disabled (unlimited growth)
- The EmbeddingCache shall log the number of entries evicted and the resulting cache size

### Lockfile

- The compaction shall acquire a lockfile at `{cachePath}/.compaction.lock` before starting
  - When the lockfile exists and the PID within is alive, compaction shall skip (another host is compacting)
  - When the lockfile exists and the PID within is dead, the lockfile shall be deleted and compaction shall proceed
  - When the lockfile cannot be acquired, compaction shall skip silently (not an error)
- The lockfile shall contain the PID and timestamp of the acquiring process
- The lockfile shall be deleted when compaction completes (success or failure)

### Startup Trigger

- When the host starts and the cache is enabled, the EmbeddingCache shall check file count and trigger compaction if above threshold
- The startup compaction shall run on a background thread and not block host initialization

### Configuration

- `CompactionThreshold` shall default to 100 files
- `MaxSizeMb` shall default to 500 MB
- Both shall be configurable via `EmbeddingCacheSettings`

## Constraints

- **Compaction is best-effort** — if it can't acquire the lock or fails partway, the cache continues working with uncompacted files (design: acceleration only)
- **No file locking for reads** — compaction must be safe for concurrent readers. Write new file first, then delete old files (design: concurrent hosts)
- **Lockfile is per-cache-path** — each cache directory has its own lockfile

## References

- [Embedding Cache Design](../../../designs/future/embedding-cache.md) — concurrency model, error handling, compaction section
- [Plan: 01-local-cache](01-local-cache.md) — prerequisite, establishes `EmbeddingCache` and parquet schema
- [Testing guidelines](../../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions

## Error Policy

Maintenance failures shall never affect cache read/write operations. When maintenance fails:
1. Log warning with details (lockfile contention, write failure, etc.)
2. Leave existing cache files intact
3. Retry on next trigger (startup or threshold exceeded)

The cache works correctly with any number of uncompacted files. Maintenance is optimization, not correctness.
