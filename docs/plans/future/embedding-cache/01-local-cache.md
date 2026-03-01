# Plan: Local Embedding Cache

Implements: [Embedding Cache Design](../../../designs/future/embedding-cache.md) — EmbeddingCache, CachingEmbeddingProvider, configuration, DI registration

## Scope

**Covers:**
- `EmbeddingCache` class — single-path parquet read/write
- `CachingEmbeddingProvider` class — `IEmbeddingProvider` decorator
- Content hash computation (`SHA256(model + "\0" + type + "\0" + text)`)
- `EmbeddingCacheSettings` configuration class
- DI registration wrapping existing provider waterfall
- `help://` documentation for embedding cache
- Tests for all components

**Does not cover:**
- Compaction and eviction (Plan: 02-cache-maintenance)
- Multi-path / shared cache resolution (Plan: 03-layered-resolution)
- Matryoshka dimensional truncation (future — no current model requires it)
- Cloud cache (future consideration, not planned)
- `::cache` command (follow-on work)

## Enables

Once the local cache exists:
- **Cross-repo embedding reuse** — importing `github://company/docs` in a second repo hits the cache for every file
- **Re-index without recompute** — unchanged files get cache hits even after host restart
- **Plan: 02-cache-maintenance** can proceed — compaction and eviction operate on the cache this plan creates
- **Plan: 03-layered-resolution** can proceed — extends single-path to multi-path

This is the foundation. Plans 02 and 03 extend it but are not required for the cache to deliver value.

## Prerequisites

- Existing `IEmbeddingProvider` interface and provider implementations (already exist)
- Existing DI registration waterfall in `RepoIndexerServiceCollectionExtensions` (already exists)
- Parquet read/write via a dedicated in-memory DuckDB connection (`read_parquet`/`COPY TO`) — see design doc Parquet I/O Strategy section
- `RepoQlConfig` settings infrastructure with `[Setting]` attributes (already exists)

## North Star

An embedding computed in one repository is immediately available in every other repository on the same machine. Cache lookup adds negligible latency to the embedding pipeline. Cache misses are invisible — the provider computes normally.

## Done Criteria

### EmbeddingCache

- The EmbeddingCache shall write cache entries to parquet files in the configured cache directory
  - Each write shall create a new file named `{yyyyMMdd-HHmmss}-{pid}-{seq}.parquet` (sequence number prevents same-second collisions)
  - Writes shall use atomic temp-file-then-rename to prevent readers from seeing partial files
  - When the cache directory does not exist, it shall be created on first write
  - When the provider returns null for a text, that text shall not be written to the cache
- The EmbeddingCache shall look up cached embeddings by content hash and model
  - When a hash is found, return the cached embedding and max dimension
  - When a hash is not found, return no result for that hash
- The EmbeddingCache shall support batch lookups efficiently
  - When given N hashes, return results for all hits in a single operation
- The EmbeddingCache shall store vectors at the provider's full dimensionality
- The EmbeddingCache shall be thread-safe for concurrent read and write operations

### CachingEmbeddingProvider

- The CachingEmbeddingProvider shall implement `IEmbeddingProvider`
- The CachingEmbeddingProvider shall delegate `Model`, `Dimension`, and `Enabled` to the inner provider
- When embedding a batch of texts, the CachingEmbeddingProvider shall:
  1. Compute content hashes for all texts
  2. Look up hashes in the cache
  3. Delegate only cache misses to the inner provider
  4. Write newly computed embeddings back to the cache
  5. Return merged results (cache hits + newly computed) in original order
- When all texts hit the cache, the inner provider shall not be called
- When no texts hit the cache, behavior shall be identical to the inner provider alone
- The CachingEmbeddingProvider shall correctly distinguish passage and query embeddings in cache keys
  - `EmbedPassageAsync` and `EmbedQueryAsync` for the same text shall produce different cache keys
  - `EmbedPassageBatchAsync` and `EmbedQueryBatchAsync` for the same texts shall produce different cache keys
- The CachingEmbeddingProvider shall pass through `BatchEmbeddingProgress` to the inner provider
  - Progress shall reflect only the miss count, not the total count
- The CachingEmbeddingProvider shall log cache hit rates per batch at Information level
  - Log shall include hits, total, percentage, and number of misses to compute

### Content Hashing

- The hash shall be `SHA256(model + "\0" + type + "\0" + text)` where type is `"p"` or `"q"`
- The hash shall be deterministic — same inputs produce same hash on any machine
- The hash shall use UTF-8 encoding
- The hash shall be represented as a 64-character hex string (`Convert.ToHexString`) for dictionary keys and parquet storage

### Configuration

- The `EmbeddingCacheSettings` shall be nested under `EmbeddingSettings` in `RepoQlConfig`
- The cache shall be enabled by default
  - Where `cache.enabled` is `false`, the CachingEmbeddingProvider shall pass through to the inner provider without cache operations
- The default cache path shall be `~/.repoql/embedding-cache/`

### DI Registration

- The CachingEmbeddingProvider shall wrap the existing provider in `RepoIndexerServiceCollectionExtensions`
- The CachingEmbeddingProvider shall wrap both the primary and `"local"` keyed providers
- Both providers shall share the same `EmbeddingCache` instance

### Documentation

- The plan shall include `help://` documentation for the embedding cache feature
  - How it works (content-addressed, automatic)
  - Configuration options
  - How to verify it's working (log messages, cache hit rates)

## Constraints

- **No new DuckDB tables** — cache is external parquet files, not part of the graph schema (design: schema frozen)
- **No changes to IEmbeddingProvider** — decorator pattern preserves the existing interface (design: decorator pattern)
- **No changes to VectorIndexCoordinator or DuckDbDataStore** — cache is invisible downstream (design: single integration point)
- **Cache failures never prevent embedding** — all cache errors are logged and swallowed (design: acceleration only)
- **Single cache path in this plan** — multi-path is Plan 03

## References

- [Embedding Cache Design](../../../designs/future/embedding-cache.md) — component contracts, data flow, trade-offs
- [Embedding Cache Flow](../../../flows/future/embedding-cache.md) — stages, actors, failure modes
- [IEmbeddingProvider](../../../../src/RepoQL.Contracts/Embeddings/IEmbeddingProvider.cs) — interface being decorated
- [JitEmbeddingCache](../../../../src/RepoQL.Explore/Search/JitEmbeddingCache.cs) — existing pattern for hash-based cache with batch support
- [DI Registration](../../../../src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs) — existing provider waterfall (lines ~167-374)
- [RepoQlConfig](../../../../src/RepoQL.Contracts/Configuration/RepoQlConfig.cs) — configuration structure with `[Setting]` attributes
- [Testing guidelines](../../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

Cache errors shall never prevent embedding from completing. When a cache operation fails:
1. Log the error at warning level (write failure) or debug level (read failure on optional path)
2. Continue as if the cache does not exist
3. The embedding pipeline produces identical results with or without a functioning cache

This aligns with the north-star declaration: "An agent should be able to function with no cache, a cold cache, or a fully warm cache — the only difference is speed."
