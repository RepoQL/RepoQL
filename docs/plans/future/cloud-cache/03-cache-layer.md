---
description: EmbeddingCacheLayer — DuckDB lookup and staging write in the embedding service. Eventarc triggers the writer automatically.
tags: [plan, cloud-cache, cache-layer, duckdb, grpc]
audience: { human: 35, agent: 65 }
categories: ["Plan[95%]", "Design[5%]"]
---

# Plan: Cache Layer

Implements: [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — EmbeddingCacheLayer, CacheLayerSettings, Integration into EmbeddingServiceImpl, Cache Key, DuckDB GCS Authentication, Vector Type Boundary

## Scope

**Covers:**
- `EmbeddingCacheLayer` class — lookup, write-back, DuckDB lifecycle
- `CacheLayerSettings` configuration — buckets, enabled flag, optional `DirectWriterUrl` for local dev
- `ChunkFingerprint` and `CacheLookupResult` records
- Content fingerprinting — `SHA256(context + "\0" + chunk_content)`
- DuckDB embedded instance with httpfs for GCS parquet reads
- Cache lookup via `DISTINCT ON (sha256)` query against source+model shard
- Staging write — parquet file to staging bucket with source and model in path (Eventarc triggers the writer automatically on OBJECT_FINALIZE)
- Vector type boundary — int8/float conversion at cache read/write
- Integration into `EmbeddingServiceImpl.EmbedChunks`
- DI registration — opt-in via `CacheLayerSettings.Enabled`
- Tests for lookup, write-back, fingerprinting, and type conversion

**Does not cover:**
- GCS bucket creation or IAM (Plan: 01-infrastructure — prerequisite)
- Proto changes (Plan: 02-proto-source-resolution — prerequisite)
- Writer service that processes staging files (Plan: 04-writer-service)
- Compaction (Plan: 05-compaction)

## Enables

Once the cache layer exists:
- **Cache hits on the hot path** — repeated embeddings for the same source + content skip Voyage
- **Cost reduction proportional to shared code** — 100 developers on one repo ≈ 1x Voyage cost
- **End-to-end latency improvement** — cache hit: ~100-150ms vs cache miss: ~600-2200ms
- **Staging pipeline feeds writer** — new vectors flow to permanent storage via Eventarc (triggered by staging upload)

## Prerequisites

- Plan 01 complete — GCS buckets exist, IAM configured, HMAC keys in Secret Manager
- Plan 02 complete — `source` field available in `EmbedChunksRequest`
- DuckDB .NET bindings available — `DuckDB.NET.Data` NuGet package

## North Star

Cache lookup adds less than 150ms to the hot path. Cache miss is indistinguishable from no-cache behavior. Cache failure is invisible to the client — Voyage serves as the fallback, always.

## Done Criteria

### Fingerprinting

- The cache layer shall compute `SHA256(context + "\0" + chunk_content)` for each chunk
  - Where `context` is the document context from the `ChunkGroup`
  - Where `chunk_content` is the individual chunk text
- The fingerprint shall be a lowercase hex string (64 characters)
- When context is empty, the hash shall be `SHA256("\0" + chunk_content)` (consistent key, no special case)

### Cache Lookup

- The cache layer shall query `gs://embeddings/source={source_hash}/model={model}/*.parquet` via DuckDB httpfs
- The query shall use `DISTINCT ON (sha256) ORDER BY created_at DESC` for deterministic dedup
- The query shall filter by `sha256 IN (...)` using the batch's fingerprints
- The cache layer shall partition results into hits (sha256 → int8 vector) and misses (sha256 → text + context)
- When GCS is unreachable, the cache layer shall treat the entire batch as misses
  - The exception shall be caught and logged, not propagated
- When no parquet files exist for the shard (new repo), the cache layer shall return all misses
- The DuckDB instance shall set `enable_object_cache=true` for parquet footer caching

### DuckDB Lifecycle

- The cache layer shall create one DuckDB in-memory instance at construction
- The DuckDB instance shall be configured with httpfs and GCS HMAC credentials
- The DuckDB instance shall be disposed when the cache layer is disposed
- The cache layer shall be registered as a singleton in DI (one DuckDB instance per service lifetime)

### Staging Write

- The cache layer shall write new vectors as a parquet file to the staging bucket
- The staging path shall be `source={source_hash}/model={model}/instance-{instance_id}-{uuid}.parquet`
- The parquet schema shall have columns: `sha256 VARCHAR`, `vector TINYINT[]`, `created_at TIMESTAMP`
- The staging write shall convert float vectors to int8 (narrowing cast, validated)
  - If any value is outside [-128, 127], log error and skip that vector
- The staging write shall be fire-and-forget — failures logged, not propagated to the caller

### Vector Type Boundary

- When reading from cache, the cache layer shall widen `TINYINT[]` to `float[]` for the gRPC response
- When writing to staging, the cache layer shall narrow `float[]` to `TINYINT[]` (values are already integer-valued from Voyage int8 output)
- The narrowing cast shall validate no truncation occurs

### Integration

- The cache layer shall be called from `EmbeddingServiceImpl.EmbedChunks` when `Enabled` is true and `source` is non-empty
- When all chunks are cache hits, the service shall return without calling Voyage
- When some chunks are misses, the service shall call Voyage for misses only, then merge results
- The response shall preserve original chunk ordering regardless of hit/miss partition
- `EmbedQuery` shall not use the cache — query embeddings are low-volume and have no source context

### Configuration

- `CacheLayerSettings.Enabled` shall default to `false`
- When `Enabled` is false or settings are incomplete, the service shall behave as today (direct Voyage relay)
- Settings shall be injected via standard .NET configuration (`IOptions<CacheLayerSettings>`)

## Constraints

- **Inline layer, not decorator** — design chose inline because the cache needs the `source` field from gRPC request and operates on flat chunks, not contextual groups
- **DuckDB is a new dependency** — adds ~1-2s cold start and ~50-100MB memory. Acceptable for Cloud Run with minimum instances = 1 in production
- **HMAC keys, not workload identity** — DuckDB httpfs only supports S3-compatible auth; native GCP workload identity doesn't work
- **Fire-and-forget write-back** — staging write must never delay the response. Use `Task.Run` or equivalent, catch all exceptions. No explicit dispatch needed — Eventarc triggers the writer on staging upload
- **No `EmbedQuery` caching** — design explicitly excluded query embeddings (low-volume, no source context)

## References

- [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — contracts, cache key, vector type boundary, DuckDB auth
- [Cloud Cache Flows: Embedding Request](../../../flows/future/cloud-cache/embedding-request.md) — hot path stages
- [`EmbeddingServiceImpl.cs`](../../../src/RepoQL.Embedding.Service/EmbeddingServiceImpl.cs) — integration point
- [`embedding.proto`](../../../src/RepoQL.Embedding.Proto/Protos/embedding.proto) — gRPC contract
- [DuckDB .NET](https://github.com/Giorgi/DuckDB.NET) — `DuckDB.NET.Data` NuGet package
- [DuckDB httpfs](https://duckdb.org/docs/extensions/httpfs/s3api) — GCS via S3-compatible API

## Error Policy

Cache errors never reach the client. Every cache failure falls through to Voyage:

| Failure | Behavior |
|---------|----------|
| DuckDB query exception | Log, treat batch as all misses |
| GCS unreachable | Log, treat batch as all misses |
| Staging write fails | Log warning, response unaffected |
| Vector narrowing validation fails | Log error, skip that vector in staging write |
| DuckDB initialization fails | Log error, cache disabled for service lifetime |
