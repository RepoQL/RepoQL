# Embedding Cache Design

## North Star

Compute an embedding once. Use it everywhere, forever — until the content or the model changes.

See [north-star/embedding-cache.md](../../north-star/embedding-cache.md) for the full vision.

## Context

Embeddings are deterministic — same model + same text = same vector (on CPU; GPU execution providers like CUDA/DML may produce slightly different floating-point results due to operation ordering, which is acceptable for approximate nearest-neighbor search but worth noting). But today, every repository independently computes embeddings for all its content. Re-indexing unchanged files recomputes them. Importing `github://company/docs` in three repos pays the cost three times. A new team member waits for a full embedding pass on code that every other developer has already embedded.

A content-addressed cache eliminates this waste. The same content with the same model produces the same cache key regardless of file path, repository, or machine.

**Enables:** [Embedding Cache Flow](../../flows/future/embedding-cache.md)

**Built on:** [IEmbeddingProvider](../../../src/RepoQL.Contracts/Embeddings/IEmbeddingProvider.cs) — existing provider interface, unchanged

## Constraints

- **Single-writer preserved** — cache uses its own parquet files, never touches the main DuckDB
- **Zero config** — local cache auto-enabled at `~/.repoql/embedding-cache/`
- **Acceleration only** — cache miss = compute normally. Cache unavailable = compute normally. Never correctness-critical
- **Schema frozen** — no new tables. The cache is external to the graph
- **Concurrent hosts** — multiple repo hosts may run simultaneously, all reading/writing the same cache directory
- **Read-only shared caches** — shared layers are never written to by the embedding pipeline. Hits from shared layers are written through to local cache to avoid repeated network reads

---

## Components

```
┌───────────────────────────────────────────────────────────┐
│               EmbeddingCoordinator                        │
│  (unchanged — calls IEmbeddingProvider as before)         │
└───────────────────────────────────────────────────────────┘
                            │
                            ▼
┌───────────────────────────────────────────────────────────┐
│              CachingEmbeddingProvider                      │
│  - Implements IEmbeddingProvider (decorator)               │
│  - Computes content hashes                                │
│  - Checks cache before delegating                         │
│  - Writes back new embeddings to local cache              │
└───────────────────────────────────────────────────────────┘
           │                              │
           ▼                              ▼
┌─────────────────────┐      ┌─────────────────────┐
│  EmbeddingCache     │      │  Inner Provider     │
│                     │      │  (ONNX / OpenRouter) │
│  - Read layers      │      │                     │
│  - Write local      │      │  - Compute vectors  │
│  - Compaction       │      │  - Model/Dim info   │
└─────────────────────┘      └─────────────────────┘
           │
           ▼
┌─────────────────────────────────────────────────────────┐
│                  Parquet Files                            │
│                                                          │
│  ~/.repoql/embedding-cache/*.parquet     (local, r/w)    │
│  //server/share/embeddings/*.parquet     (shared, r/o)   │
└─────────────────────────────────────────────────────────┘
```

The decorator is the only new component visible to the rest of the system. `EmbeddingCoordinator`, `DuckDbDataStore`, and all consumers of `IEmbeddingProvider` are unchanged.

---

## Contracts

### CachingEmbeddingProvider

```csharp
/// <summary>
/// Decorator that checks a content-addressed parquet cache before
/// delegating to the inner embedding provider. Writes new embeddings
/// back to the local cache after computation.
///
/// Complexity: content hashing, layered cache lookup, dimensional
/// truncation, write-back. All contained behind IEmbeddingProvider.
/// </summary>
public sealed class CachingEmbeddingProvider : IEmbeddingProvider
{
    public CachingEmbeddingProvider(
        IEmbeddingProvider inner,
        EmbeddingCache cache);

    // Delegates: Model, Dimension, Enabled from inner provider
    public string Model => _inner.Model;
    public int Dimension => _inner.Dimension;
    public bool Enabled => _inner.Enabled;

    // All Embed* methods: hash → lookup → hit? return : compute → write-back → return
    // BatchEmbeddingProgress is adjusted to reflect only miss count,
    // so downstream progress reporting is accurate (not inflated by cache hits).
    // Cache hit rates are logged per batch at Information level:
    //   "Embedding cache: {Hits}/{Total} hits ({Percent}%), {Misses} to compute"
}
```

### EmbeddingCache

```csharp
/// <summary>
/// Content-addressed embedding cache backed by parquet files.
/// Supports layered read paths (local, shared, remote) with
/// write-back to local only.
///
/// Complexity: parquet I/O, layered resolution, compaction,
/// concurrent access from multiple hosts.
/// </summary>
public sealed class EmbeddingCache
{
    public EmbeddingCache(EmbeddingCacheSettings settings);

    /// <summary>
    /// Look up cached embeddings for a batch of content hashes.
    /// Checks layers in priority order. First hit wins per hash.
    /// Reads parquet files via a dedicated in-memory DuckDB connection
    /// (separate from the repo's DuckDbDataStore).
    /// </summary>
    /// <returns>
    /// Dictionary of hex hash string → (embedding, maxDim) for cache hits.
    /// Missing keys are cache misses.
    /// </returns>
    Task<Dictionary<string, CachedEmbedding>> LookupAsync(
        IReadOnlyList<string> contentHashes,
        string model,
        CancellationToken ct = default);

    /// <summary>
    /// Append newly computed embeddings to the local cache.
    /// Creates a new parquet file via atomic temp-file-then-rename.
    /// Never modifies existing files. Null embeddings are not cached.
    /// </summary>
    Task WriteBackAsync(IReadOnlyList<CacheEntry> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Merge parquet files, deduplicate, evict oldest entries.
    /// Acquires lockfile. No-op if another host is compacting.
    /// </summary>
    Task CompactAsync(CancellationToken ct = default);

    // GetStats() — deferred until observability work requires it
}

public record CachedEmbedding(float[] Embedding, int MaxDim);

public record CacheEntry(
    string TextHash,        // Hex-encoded SHA256(model + "\0" + type + "\0" + text)
    string Model,           // for human inspection
    int MaxDim,             // full dimensionality of stored vector
    float[] Embedding,      // full-dimensional vector
    DateTimeOffset CreatedAt);

```

**Key type:** Content hashes use hex-encoded strings (`Convert.ToHexString`), not `byte[]`. This avoids the reference-equality trap in .NET dictionaries (`byte[]` lacks value equality without a custom comparer). Hex strings are 64 characters, trivially comparable, and debugger-friendly.

**Async methods:** Cache operations involve parquet I/O (potentially on network paths for shared caches). The `IEmbeddingProvider` interface is fully async, so the cache should not block.

### Configuration

```csharp
public sealed class EmbeddingCacheSettings
{
    [Setting("Enable embedding cache",
        DefaultValue = "true")]
    public bool Enabled { get; set; } = true;

    [Setting("Cache directory paths (first is write target, all are read)",
        DefaultValue = "~/.repoql/embedding-cache/")]
    public List<string> Paths { get; set; } = ["~/.repoql/embedding-cache/"];

    [Setting("Maximum cache size in MB (0 = unlimited)",
        DefaultValue = "500")]
    public int MaxSizeMb { get; set; } = 500;

    [Setting("Compact when file count exceeds threshold",
        DefaultValue = "100")]
    public int CompactionThreshold { get; set; } = 100;
}
```

Added to `RepoQlConfig.EmbeddingSettings` as a nested property:

```csharp
public sealed class EmbeddingSettings
{
    // ... existing properties ...

    [Setting("Embedding cache settings")]
    public EmbeddingCacheSettings? Cache { get; set; }
}
```

---

## Cache Key

The cache key is `SHA256(model + "\0" + type + "\0" + text)` where:

| Component | Value | Purpose |
|-----------|-------|---------|
| `model` | `"intfloat/e5-small-v2"` | Different models → different keys |
| `type` | `"p"` or `"q"` | Passage vs query → different vectors (asymmetric encoding) |
| `text` | Raw text before prefix | The content being embedded |

The null bytes prevent component collisions (e.g., model `"ab"` + text `"cd"` vs model `"a"` + text `"bcd"`).

**Why model in the key, not dimension:** A model produces one vector at its native dimensionality. Matryoshka truncation happens on read, not on compute. The key identifies "this model's embedding of this text" — dimension is a property of how the cached vector is consumed, not what it is.

**Why type in the key:** E5 models prepend `"passage: "` or `"query: "` before encoding, producing different vectors for the same text. The cache must distinguish these. The `CachingEmbeddingProvider` knows which method was called (`EmbedPassageAsync` vs `EmbedQueryAsync`) and includes the type in the hash.

---

## Parquet Schema

Each parquet file contains rows with this schema:

| Column | Type | Notes |
|--------|------|-------|
| `text_hash` | `VARCHAR` | Hex-encoded SHA256 (64 chars) — the lookup key |
| `model` | `VARCHAR` | For human inspection and compaction |
| `max_dim` | `INTEGER` | Dimensionality of stored vector |
| `embedding` | `FLOAT[]` | Full-dimensional vector |
| `created_at` | `TIMESTAMP` | For eviction — oldest entries dropped first during compaction |

Vectors are stored at the model's maximum dimensionality. A 384-dim model stores 384 floats. A future 768-dim matryoshka model stores 768 floats. Consumers truncate on read.

**Null embeddings are not cached.** When a provider returns `null` (e.g., OpenRouter on whitespace input), the text is skipped during write-back. A null result means "try again," not "this text has no embedding."

**Row size:** 64 bytes (hex hash) + ~30 bytes (model string) + 4 bytes (dim) + 1,536 bytes (384 × 4-byte floats) + 8 bytes (timestamp) ≈ 1.6 KB uncompressed per entry. Parquet columnar compression typically achieves 3-5× reduction. The 500 MB `MaxSizeMb` limit measures on-disk (compressed) size, so the effective capacity is higher than the naive estimate of 320K entries.

---

## Parquet I/O Strategy

`EmbeddingCache` uses a **dedicated in-memory DuckDB connection** for all parquet operations, separate from the repo's `DuckDbDataStore`. This avoids contention with the single-writer repo database while leveraging DuckDB's native `read_parquet` for efficient lookups and `COPY TO` for writing.

The dedicated connection is:
- Created once at `EmbeddingCache` construction (lightweight — in-memory, no persistent state)
- Used for reads (`read_parquet` with per-file error isolation) and writes (`COPY TO` with temp-file-then-rename)
- Thread-safe via internal serialization (DuckDB connections are single-threaded; use a semaphore for concurrent batch operations)

This is the same pattern as `JitEmbeddingCache` — a standalone component that owns its own state, not coupled to the repo's data store.

---

## Data Flow

### Batch Embed (Happy Path)

```
CachingEmbeddingProvider.EmbedPassageBatchAsync(texts):

    hashes = texts.Select(t => SHA256(Model + "\0p\0" + t))

    hits = cache.Lookup(hashes, Model)

    misses = texts.Where(hash not in hits)

    if misses.Any():
        computed = inner.EmbedPassageBatchAsync(misses)
        cache.WriteBack(misses.Zip(computed) → CacheEntry[])

    return MergeResults(hits, computed)
        // For hits: truncate to Dimension if max_dim > Dimension
```

### Dimensional Truncation (Cache Hit)

```
cached = hits[hash]    // e.g., 768-dim vector

if cached.MaxDim == provider.Dimension:
    return cached.Embedding

if cached.MaxDim > provider.Dimension && model.SupportsMatryoshka:
    truncated = cached.Embedding[..provider.Dimension]
    return L2Normalize(truncated)

// Cached at wrong dim, model doesn't support truncation
// Treat as miss — recompute at operational dim
```

For current models (`e5-small-v2`, 384-dim, not matryoshka), truncation never triggers. The path exists for future model upgrades.

### Write-Back

```
EmbeddingCache.WriteBackAsync(entries):
    // Filter out null embeddings — don't cache provider failures
    valid = entries.Where(e => e.Embedding is not null)
    if !valid.Any(): return

    filename = $"{timestamp:yyyyMMdd-HHmmss}-{pid}-{seq}.parquet"
    tempPath = Path.Combine(localCachePath, $".tmp-{filename}")
    finalPath = Path.Combine(localCachePath, filename)

    WriteParquetFile(tempPath, valid)
    File.Move(tempPath, finalPath)    // atomic rename

    if FileCount(localCachePath) > CompactionThreshold:
        TryCompactAsync()    // non-blocking, skips if locked
```

File names include a per-host monotonic sequence number (`seq`) to prevent collisions when multiple batches complete within the same second. Writes use temp-file-then-rename to ensure readers never see partial files.

Each host writes its own files. No coordination needed. Duplicate entries across files are harmless — `Lookup` returns the first match per hash.

---

## DI Registration

Register `EmbeddingCache` as its own singleton so both providers share one instance:

```csharp
// Shared cache instance
services.AddSingleton<EmbeddingCache>(sp =>
{
    var settings = sp.GetRequiredService<RepoQlConfig>().Embedding?.Cache
        ?? new EmbeddingCacheSettings();
    return new EmbeddingCache(settings);
});

// Primary provider — wrap with cache decorator
services.AddSingleton<IEmbeddingProvider>(sp =>
{
    // ... existing waterfall: None → OpenRouter → ONNX → Hashed ...
    IEmbeddingProvider inner = /* resolved provider */;

    var cacheSettings = sp.GetRequiredService<RepoQlConfig>().Embedding?.Cache;
    if (cacheSettings is null or { Enabled: true })
    {
        var cache = sp.GetRequiredService<EmbeddingCache>();
        return new CachingEmbeddingProvider(inner, cache);
    }

    return inner;
});
```

Same pattern for the `"local"` keyed provider — resolve the same `EmbeddingCache` singleton.

**Query embeddings are cached in parquet only when computed through `CachingEmbeddingProvider`.** The existing in-process caches (`JitEmbeddingCache`, `EmbedUdf` 60-second TTL cache) continue to operate at their own layers and are complementary — they prevent redundant cache lookups within a session, while the parquet cache prevents redundant computation across sessions and repos.

---

## Concurrency

| Scenario | Mechanism |
|----------|-----------|
| Multiple batches in same host | `EmbeddingCache.LookupAsync` is thread-safe (read-only on parquet files). `WriteBackAsync` creates uniquely-named files (timestamp + PID + sequence). |
| Multiple hosts on same machine | Each writes `{timestamp}-{pid}-{seq}.parquet`. Reads glob all files. No locking for reads. |
| Compaction vs reads | Compaction writes a new merged file via temp-file-then-rename, then deletes originals. On Windows, deletion of files with open handles will fail (protecting readers); compaction skips those files and retries next cycle. On Linux, open file handles survive deletion. |
| Compaction vs compaction | Lockfile `~/.repoql/embedding-cache/.compaction.lock`. If locked, skip. |

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Cache directory missing | Create on first write. Reads return empty (no hits). |
| Shared path unreachable | Log debug, skip layer. Not a warning — shared caches are optional. |
| Parquet file corrupted | Read fails for that file. Other files still readable. Log warning. |
| Write-back fails (disk full) | Log warning, continue. Cache is acceleration, not correctness. |
| Compaction lockfile stale | Lockfile includes PID. If PID is dead, delete lockfile and proceed. |
| Hash collision | Probability 2^-128 per pair. Wrong vector slightly degrades search quality. Self-corrects on reindex. Not worth preventing. |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Parquet directory | DuckDB file | Multiple concurrent hosts need concurrent writes. DuckDB is single-writer. Parquet files are independent. |
| Parquet directory | SQLite | DuckDB reads parquet natively. No new dependency. Parquet is the lingua franca for columnar data. |
| Decorator pattern | Modified provider | Zero changes to existing code. EmbeddingCoordinator, DuckDbDataStore, all consumers unchanged. |
| SHA256 | Shorter hash | 32 bytes per entry is negligible. Collision resistance matters for a cache that grows across repos. |
| Full-dim storage | Per-dim entries | One entry serves all consumers. Matryoshka truncation is trivial. 3× space savings. |
| Model in hash key | Separate invalidation | Model changes invalidate naturally. No purge command, no migration, no version tracking. |
| Auto-enabled | Opt-in | Cache has no correctness impact. Default-on means everyone benefits. Disable with `cache.enabled: false`. |
| Timestamped files | Single file append | No file locking. No corruption risk from concurrent writers. Compaction consolidates periodically. |
| Oldest-first eviction by `created_at` | No eviction | Unbounded growth across repos is the primary risk. 500 MB default is generous but bounded. Not true LRU (no access-time tracking in immutable parquet files), but insertion-time eviction is simple and sufficient — frequently-used embeddings are recomputed and re-cached with fresh timestamps. |

## Alternatives Considered

**Cache in `document_embedding` table:** Already stores embeddings per-repo. Adding a content hash column would enable within-repo deduplication. Rejected: doesn't solve cross-repo sharing. Violates schema-frozen constraint (new column on existing table). The cache is a separate concern from per-repo storage.

**In-memory LRU cache (like JitEmbeddingCache):** Fast, simple, already proven. Rejected: doesn't survive host restart. Doesn't share across repos. The 500-entry JitEmbeddingCache solves a different problem (search-session deduplication).

**DuckDB attach:** Main DuckDB `ATTACH`es the cache as a second database. Rejected: single-writer constraint applies per-connection. Concurrent hosts can't share an attached database.

**Content hash on document_embedding, skip cache file:** Add a `text_hash` column to `document_embedding`, query it before embedding. Rejected: per-repo only. Schema change. Doesn't address the cross-repo case that provides the most value.

## Risks

| Risk | Mitigation |
|------|------------|
| Cache grows unbounded | Default 500 MB limit. Compaction evicts oldest. Configurable. |
| Stale lockfile blocks compaction forever | Lockfile includes PID. Dead PID = stale lockfile = safe to delete. |
| Parquet read performance with many small files | Compaction consolidates. DuckDB handles parquet glob efficiently. Threshold at 100 files triggers compaction. |
| Shared cache serves wrong model's embeddings | Model is part of the hash key. Impossible by construction. |
| Cache hit returns wrong vector (hash collision) | SHA256 collision probability 2^-128. Not a practical risk. Slightly degraded search quality, not a crash. |

## Extension Points

- **`EmbeddingCacheSettings.Paths`** — add shared or remote cache layers without code changes
- **Parquet schema** — additional columns (e.g., `source_repo`, `text_length`) can be added without breaking existing files (parquet supports schema evolution)
- **Cloud cache** — future HTTP-backed reader implements the same `Lookup` interface, added as another layer
- **Cache warming** — a `::cache-warm` command could pre-populate the cache from `document_embedding` for sharing
- **Cache export** — `::cache-export` could write a single parquet file from the local cache for distribution

---

## Related

- [North Star: Embedding Cache](../../north-star/embedding-cache.md) — what great looks like
- [Flow: Embedding Cache](../../flows/future/embedding-cache.md) — stages, actors, handoffs
- [Flow: Embedding Generation](../../flows/current/indexing/embedding-generation.md) — current flow (this wraps it)
- [IEmbeddingProvider](../../../src/RepoQL.Contracts/Embeddings/IEmbeddingProvider.cs) — interface being decorated
- [JitEmbeddingCache](../../../src/RepoQL.Explore/Search/JitEmbeddingCache.cs) — existing in-process cache (different scope, complementary)
