# Cloud Embedding Cache Design

## North Star

Compute an embedding once per source. Serve it to every customer working on the same repo. Shared imports hit the cache across all customers.

See [north-star/embedding-cache.md](../../north-star/embedding-cache.md) for the full vision.

## Context

The embedding service (`EmbeddingServiceImpl`) is a thin gRPC relay: it receives chunks from RepoQL hosts, calls Voyage, and returns vectors. Every request hits Voyage, regardless of whether the same content was embedded yesterday by another customer.

Embeddings are deterministic — same model + same content = same vector. A content-addressed cache in front of Voyage eliminates redundant API calls. The cache is scoped per source (repo origin) — 100 developers on the same repo share one cache. Shared open-source imports (`github://lodash/lodash`) share one cache across all customers who import them.

**Enables:** [Cloud Cache Flows](../../flows/current/cloud-cache/)

**Extends:** [EmbeddingServiceImpl](../../../src/RepoQL.Embedding.Service/EmbeddingServiceImpl.cs) — cache logic added to the existing service, not a new service

**Infrastructure:** Pulumi (C#) — GCS, Cloud Run, IAM; Eventarc trigger via deploy workflow

## Constraints

- **Minimal host change** — hosts resolve and send `source` (one new proto field). All cache logic lives in the service. Hosts don't know whether a vector came from cache or Voyage
- **Voyage is the source of truth** — cache miss = call Voyage. Cache down = call Voyage. Never correctness-critical
- **Customer isolation at the storage level** — sharded by source, no cross-customer queries
- **IAM blast radius contained** — workers never write to the permanent embeddings bucket
- **Voyage-specific for v1** — int8 at 1024 dimensions (voyage-context-3 native output). Model in the shard path enables future providers without schema changes
- **Existing service remains deployable without cache** — cache is opt-in via configuration. Unconfigured = direct Voyage relay as today

---

## Components

```
┌──────────────────────────────────────────────────────────┐
│                   Cloud Run: Embedding Service            │
│                                                          │
│  EmbeddingServiceImpl                                    │
│    │                                                     │
│    ├─► EmbeddingCacheLayer                               │
│    │     │  SHA256 → lookup GCS     │                    │
│    │     │  hits → return           │                    │
│    │     │  misses → pass through   │                    │
│    │     │  new vectors → staging   │                    │
│    │                                                     │
│    └─► VoyageAiClient              (unchanged)           │
│          │  misses only                                  │
│          │                                               │
│  DuckDB (embedded, in-process)                           │
│    │  httpfs reads from GCS                              │
│    │  object cache for parquet footers                   │
└──────────────────────────────────────────────────────────┘
         │ write                              │ read
         ▼                                    ▼
┌─────────────────┐               ┌─────────────────────────┐
│  GCS: Staging   │               │  GCS: Embeddings        │
│  24h lifecycle  │               │  Standard storage       │
│  Standard class │               │                         │
│                 │               │  source={hash}/         │
│  source={hash}/ │               │    model={model}/       │
│    model={m}/   │               │      part-*.parquet     │
│      {uuid}.pqt │               │      _source.json       │
└─────────────────┘               └─────────────────────────┘
         │                                    ▲
         │ OBJECT_FINALIZE event (Eventarc)   │ merge
         ▼                                    │
┌─────────────────┐               ┌─────────────────────────┐
│  Eventarc       │──────────────►│  Cloud Run: Writer      │
│  (Pub/Sub)      │               │  (scales to zero)       │
└─────────────────┘               └─────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  Cloud Run Job: Compaction                               │
│  (nightly schedule via Cloud Scheduler)                  │
│  - Consolidates part files per shard                     │
│  - Evicts entries older than 6 months                    │
└─────────────────────────────────────────────────────────┘
```

Three Cloud Run deployables (embedding service, writer, compaction job). Staging-to-writer trigger via Eventarc (GCS OBJECT_FINALIZE → Pub/Sub → Cloud Run). Buckets and IAM managed by Pulumi; the Eventarc trigger is created by the deploy-embedding-writer workflow (because it references the Cloud Run service which is deployed separately from infrastructure).

---

## Contracts

### EmbeddingCacheLayer

```csharp
internal sealed class EmbeddingCacheLayer : IDisposable
{
    public EmbeddingCacheLayer(
        IOptions<CacheLayerSettings> settings,
        IObjectStorageClient storageClient,
        IHttpClientFactory httpClientFactory,
        VoyageAiClient voyage,
        ILogger<EmbeddingCacheLayer> logger);

    Task<CacheLookupResult> LookupAsync(
        string source,
        IReadOnlyList<ChunkFingerprint> fingerprints,
        CancellationToken ct = default);

    Task WriteBackAsync(
        string source,
        IReadOnlyList<CacheEntry> entries,
        CancellationToken ct = default);
}

public record ChunkFingerprint(int OriginalIndex, string Sha256, string Text, string Context);

public record CacheLookupResult(
    IReadOnlyDictionary<string, byte[]> Hits,   // sha256 → int8 vector bytes
    IReadOnlyList<ChunkFingerprint> Misses);

public record CacheEntry(string Sha256, byte[] Vector, DateTimeOffset CreatedAt);
```

### CacheLayerSettings

```csharp
public sealed class CacheLayerSettings
{
    public bool Enabled { get; set; } = false;  // opt-in
    public string StorageBackend { get; set; } = "gcs";
    public string S3Endpoint { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public string EmbeddingsBucket { get; set; } = "";
    public string StagingBucket { get; set; } = "";
    public string DirectWriterUrl { get; set; } = "";  // local dev only — bypasses Eventarc
}
```

### Writer Service

The writer is a standalone Cloud Run service triggered by Eventarc (GCS OBJECT_FINALIZE events on the staging bucket, delivered via Pub/Sub). It also accepts direct JSON posts for local development (via `DirectWriterUrl`).

```csharp
internal sealed class CacheMergeHandler
{
    Task HandleAsync(string stagingPath, CancellationToken ct);
}
```

### Compaction Job

```csharp
internal sealed class CompactionJob
{
    Task RunAsync(CancellationToken ct);
}
```

---

## Cache Key

```
SHA256(context + "\0" + chunk_content) → hex string (64 chars)
```

The key includes **both the document context and the chunk content**. This is required because voyage-context-3 uses contextual embeddings — the same chunk text produces different vectors depending on the document context it's embedded with. A context-unaware key would produce false cache hits.

Model is part of the **shard path**, not the hash:

```
gs://embeddings/source={sha256("github.com/org/repo")}/model=voyage-context-3/part-*.parquet
```

This means:
- Same context + same chunk + same model → cache hit (correct vector)
- Same chunk in a different document (different context) → cache miss (different vector, as intended)
- Same content with different models → different shards → no collision
- Model upgrade → entirely new shard → all misses → cache rebuilds naturally
- No purge command needed for model migration — old shards are evicted by compaction after 6 months of disuse

---

## Parquet Schema

| Column | Type | Notes |
|--------|------|-------|
| sha256 | VARCHAR | Hex-encoded content hash (64 chars) |
| vector | TINYINT[] | int8 embedding (1024 elements) |
| created_at | TIMESTAMP | For eviction ordering |

int8 storage is native to voyage-context-3 — no quantization, no quality loss. ~1KB per embedding.

---

## Source Identity

The source identifier determines shard placement. See `SourceNormalizer` in `RepoQL.Embedding.Client`.

| Source type | Normalized | Example |
|-------------|-----------|---------|
| `github://org/repo` | `github.com/org/repo` | `github.com/anthropics/claude-code` |
| `https://github.com/org/repo.git` | `github.com/org/repo` | Same as above |
| `git@github.com:org/repo.git` | `github.com/org/repo` | Same as above |
| `file://` with git remote | Normalize the remote URL | Falls through to remote's identity |
| `file://` without git remote | Skip cloud cache | No stable identity |

**Normalization rules:** Strip scheme, strip `.git` suffix, strip auth, convert SSH colon syntax to slash, lowercase. Result: `{host}/{path}`.

## GCS Path Convention

```
gs://{embeddings-bucket}/
  source={sha256(canonical_origin)}/
    model={model-name}/
      part-{timestamp}.parquet
      _compaction.lock             (transient, during compaction)
      _source.json                 (human-readable origin for debugging)

gs://{staging-bucket}/
  source={sha256(canonical_origin)}/
    model={model-name}/
      instance-{cloud-run-instance}-{uuid}.parquet
```

---

## DuckDB GCS Authentication

DuckDB's httpfs extension accesses GCS via the S3-compatible API, not native GCP IAM tokens. This requires HMAC keys:

```sql
CREATE OR REPLACE SECRET embedding_cache_gcs (
    TYPE GCS,
    KEY_ID '{hmac_access_id}',
    SECRET '{hmac_secret}'
);
```

HMAC keys are created per service account via Pulumi, stored in Secret Manager, and injected as Cloud Run environment variables. Each service (embedding service, writer, compaction) uses its own service account with appropriately scoped IAM.

The cache layer also supports S3-compatible backends for local development (MinIO via Aspire).

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| GCS Parquet + DuckDB httpfs | Dedicated vector database | No new infrastructure. DuckDB is embedded. Parquet is the lingua franca. |
| Eventarc (GCS events) + separate writer | Direct writes to embeddings | IAM blast radius. Single-writer prevents corruption. |
| Content-only hash + model in path | Model in hash | Shard-level model migration. Old shards evict naturally. |
| int8 native storage | float32 with quantization | Voyage produces int8 natively. No conversion, no quality loss. 4x smaller. |
| Cloud Run (scales to zero) | Always-on VM | Pay per request. Writer and compaction have bursty workloads. |
| Pulumi (C#) | Terraform (HCL) | Same language as the codebase. Strongly typed. |

---

## Related

- [North Star: Embedding Cache](../../north-star/embedding-cache.md) — what great looks like
- [Flows: Cloud Cache](../../flows/current/cloud-cache/) — embedding request, cache merge, compaction
- [Operations Guide](cloud-embedding-cache-ops.md) — deployment, secrets, troubleshooting
- [EmbeddingServiceImpl](../../../src/RepoQL.Embedding.Service/EmbeddingServiceImpl.cs) — where the cache integrates
