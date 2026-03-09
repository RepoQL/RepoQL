---
description: Cloud Run compaction job — shard locking, deduplication, eviction, part consolidation.
tags: [plan, cloud-cache, compaction, cloud-run, duckdb]
audience: { human: 35, agent: 65 }
categories: ["Plan[95%]", "Design[5%]"]
---

# Plan: Compaction

Implements: [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Compaction Job contract

## Scope

**Covers:**
- Cloud Run job triggered by Cloud Scheduler (nightly) and Cloud Tasks (threshold)
- Shard discovery — list shards exceeding part count threshold
- Shard locking — `_compaction.lock` with GCS `ifGenerationMatch` preconditions
- Part file reading via DuckDB — all parts into single result set
- Deduplication — `ROW_NUMBER() OVER (PARTITION BY sha256 ORDER BY created_at DESC)`
- Eviction — drop entries where `created_at < NOW() - INTERVAL '6 months'`
- Write compacted file as `part-0001.parquet` (sorted by sha256, zstd, 50K row groups)
- Delete old part files
- Lock release
- Corrupted part file handling — per-file try/catch, skip failures
- Dockerfile and Cloud Run job configuration
- Tests for dedup, eviction, lock lifecycle, and concurrent safety

**Does not cover:**
- GCS bucket creation or IAM (Plan: 01-infrastructure — prerequisite)
- Part file creation (Plan: 04-writer-service — upstream)
- Cache lookup (Plan: 03-cache-layer — reads the compacted output)
- Shard-level compaction parallelism (extension point, not v1)

## Enables

Once compaction exists:
- **Cache lookup performance stays O(log n)** — single sorted file instead of scanning dozens of parts
- **Storage bounded** — 6-month TTL eviction prevents unbounded growth
- **GCS list operations stay fast** — fewer objects per shard
- **Self-managing cache** — no manual intervention for storage hygiene

Without this plan, the cache from Plans 03-04 works but accumulates part files indefinitely. Lookup degrades gradually as parts accumulate. Acceptable for weeks, problematic at scale.

## Prerequisites

- Plan 01 complete — GCS embeddings bucket exists with compaction service account IAM
- Plan 04 complete or in progress — part files exist to compact
- DuckDB with httpfs for GCS parquet read/write
- Cloud Scheduler job configured (Plan 01) to trigger this job

## North Star

Compaction is invisible. Shards stay fast, storage stays bounded, and no operator ever needs to think about it. The cache manages its own size — the 6-month TTL is generous enough that eviction never surprises anyone.

## Done Criteria

### Trigger Handling

- The compaction job shall accept two trigger types:
  - Cloud Scheduler HTTP callback (nightly, all shards)
  - Cloud Tasks message with `{ "source": "{hash}", "model": "{model}" }` (threshold, single shard)
- When triggered by schedule, the job shall list all shards and process those exceeding the part count threshold
- When triggered by threshold message, the job shall process only the specified shard

### Shard Discovery

- The job shall list `source={hash}/model={model}/` prefixes in the embeddings bucket
- The job shall count part files (`part-*.parquet`) per shard
- The job shall process shards where part count exceeds 20 (configurable)
- When GCS list fails, the job shall skip and retry on next schedule

### Shard Locking

- The job shall create `_compaction.lock` using GCS `ifGenerationMatch=0` (atomic create-if-not-exists)
- The lock file shall contain `{ "instance": "{instance_id}", "started_at": "{timestamp}" }`
- When the lock already exists and is fresh (< 1 hour), the job shall skip the shard
- When the lock already exists and is stale (> 1 hour), the job shall overwrite using `ifGenerationMatch={current_generation}`
  - This prevents races — exactly one compactor wins the overwrite
- The lock shall not prevent concurrent cache merge writes — new part files can arrive during compaction

### Read All Parts

- The job shall read all `part-*.parquet` files for the shard via DuckDB
- The query shall select `sha256, vector, created_at` from all parts
- When a part file is corrupted (invalid parquet), the job shall catch the exception per-file
  - Skip the corrupted file
  - Log the file path for manual investigation
  - Compact the remaining parts

### Deduplication

- The job shall keep only the newest entry per sha256 (`ROW_NUMBER() OVER (PARTITION BY sha256 ORDER BY created_at DESC) = 1`)
- Deduplication shall be in-memory via DuckDB SQL

### Eviction

- The job shall drop rows where `created_at < NOW() - INTERVAL '6 months'`
- The TTL interval shall be configurable (default: 6 months)
- Eviction is storage hygiene — evicted entries simply recompute on next cache miss

### Write Compacted File

- The job shall write the deduplicated, evicted result as `part-0001.parquet`
- The file shall be sorted by sha256 (critical for DuckDB row group min/max statistics)
- The file shall use zstd compression with 50,000 row group size
- The file is written directly with its final name — GCS single-object atomicity ensures readers see old or new, never partial
- When GCS write fails, the job shall release the lock and exit — old parts remain valid

### Delete Old Parts

- After successful write, the job shall delete old part files (part-0002 through part-N)
- The job shall also delete any `_compacted-*` files from prior incomplete runs
- When deletion is partial (some deletes fail), the remaining parts are harmless — deduplicated on next compaction
- During deletion, concurrent cache lookups may see duplicates — `DISTINCT ON` at read time handles this

### Lock Release

- After cleanup, the job shall delete `_compaction.lock`
- When lock delete fails, the lock goes stale and is overwritten after 1 hour — self-healing

### Configuration

| Setting | Default | Purpose |
|---------|---------|---------|
| Part count threshold | 20 | Process shards exceeding this |
| TTL | 6 months | Evict entries older than this |
| Row group size | 50,000 | Parquet row group for DuckDB statistics |
| Compression | zstd | Balance of speed and ratio |
| Stale lock timeout | 1 hour | Overwrite locks older than this |

## Constraints

- **GCS has no atomic rename** — design chose to write with the final name (`part-0001.parquet`) directly, relying on GCS single-object write atomicity
- **Lock uses `ifGenerationMatch`, not distributed locks** — design chose GCS-native preconditions over external lock services (Redis, etc.)
- **Compaction doesn't block merges** — new part files can be written while compaction runs; they're picked up on the next run
- **DuckDB for parquet I/O** — same tool as the cache layer; keeps the parquet read/write logic consistent
- **No cross-shard operations** — each shard is compacted independently

## References

- [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — Compaction Job contract, configuration
- [Cloud Cache Flows: Compaction](../../../flows/future/cloud-cache/compaction.md) — stage-by-stage flow with failure modes
- [GCS preconditions](https://cloud.google.com/storage/docs/request-preconditions) — `ifGenerationMatch` for atomic lock operations
- [DuckDB COPY TO](https://duckdb.org/docs/sql/statements/copy) — parquet export with compression and row group size

## Error Policy

Compaction is best-effort. Every failure mode self-heals:

| Failure | Behavior | Self-healing |
|---------|----------|-------------|
| Lock held by active compactor | Skip shard | Picked up on next schedule |
| Stale lock (> 1 hour) | Overwrite via `ifGenerationMatch` | Atomic, no races |
| Corrupted part file | Skip file, compact the rest | Corrupted file logged for investigation |
| GCS write timeout | Release lock, exit | Old parts remain valid; retry on next schedule |
| Partial part deletion | Extra parts remain | Deduplicated on next compaction |
| Crash after write, before cleanup | Compacted + old parts coexist | Next compaction sees all, deduplicates |
| Crash after cleanup, before lock release | Lock goes stale | Overwritten after 1 hour |
