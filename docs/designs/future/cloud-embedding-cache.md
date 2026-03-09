# Cloud Embedding Cache Design

## North Star

Compute an embedding once per source. Serve it to every customer working on the same repo. Shared imports hit the cache across all customers.

See [north-star/embedding-cache.md](../../north-star/embedding-cache.md) for the full vision.

## Context

The embedding service (`EmbeddingServiceImpl`) is a thin gRPC relay: it receives chunks from RepoQL hosts, calls Voyage, and returns vectors. Every request hits Voyage, regardless of whether the same content was embedded yesterday by another customer.

Embeddings are deterministic — same model + same content = same vector. A content-addressed cache in front of Voyage eliminates redundant API calls. The cache is scoped per source (repo origin) — 100 developers on the same repo share one cache. Shared open-source imports (`github://lodash/lodash`) share one cache across all customers who import them.

**Enables:** [Cloud Cache Flows](../../flows/future/cloud-cache/)

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
│    ├─► EmbeddingCacheLayer          (new)                │
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

Three Cloud Run deployables (embedding service, writer, compaction job). Staging-to-writer trigger via Eventarc (GCS OBJECT_FINALIZE → Pub/Sub → Cloud Run). The writer still uses Cloud Tasks for compaction dispatch. Buckets and IAM managed by Pulumi; the Eventarc trigger is created by the deploy-embedding-writer workflow (because it references the Cloud Run service which is deployed separately from infrastructure).

---

## Contracts

### EmbeddingCacheLayer

```csharp
/// <summary>
/// Cache layer that intercepts embedding requests in EmbeddingServiceImpl.
/// Looks up GCS parquet via embedded DuckDB, passes misses to Voyage,
/// writes new vectors to staging.
///
/// Complexity: SHA256 hashing, DuckDB httpfs queries, GCS staging writes.
/// All contained within EmbeddingServiceImpl.
/// </summary>
internal sealed class EmbeddingCacheLayer : IDisposable
{
    public EmbeddingCacheLayer(
        CacheLayerSettings settings,
        string model,   // from GetModelInfo at startup — stable for the service lifetime
        ILogger<EmbeddingCacheLayer> logger);

    /// <summary>
    /// Partition a batch into cache hits and misses.
    /// Reads from GCS via DuckDB httpfs.
    /// </summary>
    Task<CacheLookupResult> LookupAsync(
        string source,
        IReadOnlyList<ChunkFingerprint> fingerprints,
        CancellationToken ct = default);

    /// <summary>
    /// Write newly computed vectors to staging.
    /// Eventarc triggers the writer on OBJECT_FINALIZE — no explicit dispatch needed.
    /// Fire-and-forget — failures don't affect the response.
    /// </summary>
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
    public string EmbeddingsBucket { get; set; } = "";
    public string StagingBucket { get; set; } = "";
    public string DirectWriterUrl { get; set; } = "";  // local dev only — bypasses Eventarc
}
```

### Writer Service

The writer is a standalone Cloud Run service triggered by Eventarc (GCS OBJECT_FINALIZE events on the staging bucket, delivered via Pub/Sub). It also accepts direct JSON posts for local development (via `DirectWriterUrl`).

```csharp
/// <summary>
/// Processes staging files into permanent embeddings shards.
/// Receives Eventarc CloudEvent payloads (production) or direct JSON (local dev).
/// The MergeEndpoint detects CloudEvent format via the ce-type header.
/// Idempotent — safe to retry.
/// </summary>
internal sealed class CacheMergeHandler
{
    /// <summary>
    /// Read staging parquet, write new part to embeddings shard,
    /// delete staging file.
    /// </summary>
    Task HandleAsync(string stagingPath, CancellationToken ct);
}
```

### Compaction Job

```csharp
/// <summary>
/// Cloud Run job triggered by Cloud Scheduler.
/// Iterates shards, consolidates parts, evicts expired entries.
/// </summary>
internal sealed class CompactionJob
{
    /// <summary>
    /// Compact all shards exceeding part threshold.
    /// Evict entries older than 6 months.
    /// </summary>
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

**Why context in the hash:** Contextual embeddings are the whole point of voyage-context-3 — chunks embedded with document awareness produce better search results. Caching without context would serve wrong vectors. Within a repo, context (x-ray summaries) is stable for unchanged files, so hit rates remain high.

**Why model in the shard path, not the hash:** The cloud cache is sharded by model in the path hierarchy. Model in the hash would be redundant and would prevent shard-level model migration.

**Why no dimensions in the key:** voyage-context-3 produces 1024-dim int8. That's the only dimension this model outputs. Matryoshka truncation happens client-side if a RepoQL host wants fewer dimensions. The cache stores what the model produces.

**Why hash the source in the path:** Customer repo origins (e.g., `github.com/acme-corp/internal-platform`) are sensitive metadata. Hashing keeps GCS object names opaque while preserving stable shard identity. A `_source.json` metadata file per shard allows authorized debugging without exposing origins in the bucket listing.

---

## Parquet Schema

```
| Column       | Type      | Notes                              |
|-------------|-----------|-------------------------------------|
| sha256      | VARCHAR   | Hex-encoded content hash (64 chars) |
| vector      | TINYINT[] | int8 embedding (1024 elements)      |
| created_at  | TIMESTAMP | For eviction ordering               |
```

int8 storage is native to voyage-context-3 — no quantization, no quality loss. ~1KB per embedding.

Files are sorted by sha256 at write time. DuckDB uses row group min/max statistics to skip irrelevant groups — point queries are O(log n) without any external index.

---

## Source Identity

The source identifier determines shard placement — which parquet files a query touches. It must be:
- **Stable** — same repo produces the same identifier across machines and customers
- **Opaque** — customer repo paths don't leak into GCS object names
- **Globally meaningful** — enables cross-customer cache hits for shared repos

| Source type | Normalized | Example |
|-------------|-----------|---------|
| `github://org/repo` | `github.com/org/repo` | `github.com/anthropics/claude-code` |
| `https://github.com/org/repo.git` | `github.com/org/repo` | Same as above |
| `git@github.com:org/repo.git` | `github.com/org/repo` | Same as above |
| `https://gitlab.com/org/repo` | `gitlab.com/org/repo` | |
| `https://bitbucket.org/org/repo` | `bitbucket.org/org/repo` | |
| `https://dev.azure.com/org/proj/_git/repo` | `dev.azure.com/org/proj/_git/repo` | Full path preserved |
| `https://gitea.company.com/team/repo` | `gitea.company.com/team/repo` | Self-hosted works too |
| `file://` with git remote | Normalize the remote URL | Falls through to remote's identity |
| `file://` without git remote | Skip cloud cache | No stable identity. Local cache still works. |

**Normalization rules:** Strip scheme (`https://`, `git@`, `github://`, etc.). Strip `.git` suffix. Strip auth/credentials. Convert SSH colon syntax (`host:path`) to slash. Lowercase. Result: `{host}/{path}` — the full path after the host is preserved, not assumed to be `{owner}/{repo}`.

This works for any git host regardless of URL structure. A single repo referenced via HTTPS, SSH, or a RepoQL scheme all normalize to the same identity — one shard, maximum cache hits.

The source identifier sent to the embedding service is the **normalized origin** (e.g., `github.com/org/repo`), resolved by the RepoQL host before the gRPC call. The embedding service hashes it (`SHA256`) for the shard path.

A metadata file (`_source.json`) in each shard maps hash → human-readable origin for debugging and diagnostics.

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

Source identifiers are SHA256-hashed, producing uniform 64-character hex paths. No URL-encoding concerns, no path length issues, no customer data in object names.

---

## Proto Changes

The existing `EmbedChunksRequest` needs a source identifier for shard placement.

```protobuf
message EmbedChunksRequest {
  repeated ChunkGroup groups = 1;
  // Canonical repo origin for cache shard placement.
  // Normalized: "{host}/{path}" (e.g., "github.com/org/repo").
  // Empty = skip cloud cache (e.g., file:// with no git remote).
  string source = 2;
}
```

One field added. No new RPCs, no streaming. The `source` field is additive — existing clients that don't set it get empty string, which skips the cloud cache. No breaking change.

**Why not streaming:** Bidi streaming is the hardest gRPC pattern to get right — connection pinning defeats Cloud Run autoscaling, partial failure recovery requires client-side ack tracking, and observability is murkier (what's the "latency" of a 5-minute stream?). The pipeline benefit comes from the host sending **multiple concurrent unary requests** over one HTTP/2 connection instead:

```
Host sends:   batch1 ──────►  batch2 ──────►  batch3 ──────►
Service:      ◄── response1   ◄── response2   ◄── response3
              (multiplexed on one HTTP/2 connection)
```

Same overlap, trivial retry per batch, clean per-request metrics, battle-tested pattern. Streaming can be added later as a new RPC if batching boundaries become a bottleneck — easier to add than to fix.

---

## Integration into EmbeddingServiceImpl

The cache is added to the existing `EmbedChunks` method, not as a decorator but as an inline layer. This is different from the local cache (which uses the decorator pattern on `IEmbeddingProvider`) because:

1. The cloud cache needs the `source` identifier, which is in the gRPC request but not in `IEmbeddingProvider`
2. The cache operates on flat chunks, not contextual groups — it needs access before group assembly

```csharp
public override async Task<EmbedChunksResponse> EmbedChunks(
    EmbedChunksRequest request, ServerCallContext context)
{
    // Existing validation unchanged...

    if (_cache is { Enabled: true } && !string.IsNullOrEmpty(request.Source))
    {
        var fingerprints = BuildFingerprints(request);
        var lookup = await _cache.LookupAsync(
            request.Source, fingerprints, context.CancellationToken);

        if (lookup.Misses.Count == 0)
            return BuildResponse(lookup.Hits, request);

        var computed = await ComputeMisses(lookup.Misses, context);
        _ = _cache.WriteBackAsync(request.Source, computed);
        return BuildResponse(lookup.Hits, computed, request);
    }

    // No source or cache disabled — direct Voyage relay as today
    return await ComputeAll(request, context);
}
```

---

## Infrastructure (Pulumi)

```csharp
var env = Pulumi.Deployment.Instance.StackName;  // dev, staging, prod

// Embeddings bucket — Standard, read-heavy, never written by workers
var embeddingsBucket = new Gcp.Storage.Bucket($"repoql-embeddings-{env}", new()
{
    Location = "US",
    StorageClass = "STANDARD",
    UniformBucketLevelAccess = true,
});

// Staging bucket — Standard, 24h lifecycle, write-heavy
var stagingBucket = new Gcp.Storage.Bucket($"repoql-staging-{env}", new()
{
    Location = "US",
    StorageClass = "STANDARD",
    UniformBucketLevelAccess = true,
    LifecycleRules = new[]
    {
        new Gcp.Storage.Inputs.BucketLifecycleRuleArgs
        {
            Action = new() { Type = "Delete" },
            Condition = new() { Age = 1 },  // 24h cleanup
        },
    },
});

// IAM: embedding service can read embeddings, write staging
// IAM: embedding service SA has eventarc.eventReceiver (for Eventarc trigger auth)
// IAM: GCS service agent has pubsub.publisher (to publish OBJECT_FINALIZE events)
// IAM: Pub/Sub service agent has iam.serviceAccountTokenCreator (to authenticate push delivery)
// IAM: writer service can read staging, write embeddings, delete staging
// IAM: compaction job can read/write embeddings
//
// Note: The Eventarc trigger itself is created by the deploy-embedding-writer workflow,
// not Pulumi, because it references the Cloud Run writer service which is deployed separately.
```

Three Pulumi stacks: `dev`, `staging`, `prod`. Same code, different config. `pulumi up` per environment.

---

## Vector Type Boundary

The int8/float conversion happens at a specific point in the data path:

```
Voyage API → int8 values (parsed as float32 by VoyageAiClient)
  → gRPC response → repeated float (wire format, float32)
  → Cloud cache → TINYINT[] in parquet (int8, 1KB per vector)
  → Cache hit → read int8, cast to float32 for gRPC response
```

- **Voyage returns int8 values** when `output_dtype: "int8"` is set. `VoyageAiClient` deserializes these as `float` (each value is -128 to 127).
- **The gRPC proto uses `repeated float`** — this is the wire contract. No proto change needed.
- **The cache stores `TINYINT[]`** — the float values are narrowed to int8 on cache write (lossless, since they're already integer-valued). On cache read, they're widened back to float32 for the gRPC response.
- **The narrowing cast is safe** because Voyage's int8 output produces values in [-128, 127]. A validation check on write ensures no truncation.

**EmbedQuery is not cached.** Query embeddings are low-volume (one per search), have no source context, and the latency savings (~500ms) don't justify the complexity. The cache is for the high-volume `EmbedChunks` path only.

---

## DuckDB GCS Authentication

DuckDB's httpfs extension accesses GCS via the S3-compatible API, not native GCP IAM tokens. This requires HMAC keys configured on a service account:

```sql
SET s3_endpoint = 'storage.googleapis.com';
SET s3_access_key_id = '{hmac_access_id}';
SET s3_secret_access_key = '{hmac_secret}';
SET s3_region = 'auto';
SET s3_url_style = 'path';
SET enable_object_cache = true;
```

HMAC keys are created on the Cloud Run service account via `gsutil hmac create`. The keys are injected as Cloud Run environment variables (or Secret Manager references). Each service (embedding service, writer, compaction) uses its own service account with appropriately scoped IAM, and its own HMAC keys.

This is the only way DuckDB can authenticate to GCS. Native GCP workload identity doesn't work with httpfs.

**DuckDB as a new dependency:** The embedding service currently has no DuckDB dependency. Adding embedded DuckDB + httpfs increases Cloud Run cold start time (~1-2s) and memory footprint (~50-100MB). This is acceptable for a service that scales to zero — the first request after idle pays the cold start, subsequent requests are fast. Cloud Run minimum instances (1) eliminates this for production.

---

## Cross-Cutting Concerns

### Observability

| Signal | What it tells you |
|--------|-------------------|
| Cache hit rate per batch | Is the cache working? Should trend up over time. |
| Voyage API calls saved | Direct cost savings. |
| P99 latency: hit vs miss | Cache hits should be 3-10x faster. |
| Staging bucket depth | Are writes keeping up? Should trend to zero. |
| Part count per shard | Are compaction triggers firing? |
| Shard size over time | Is eviction working? |

All metrics via Cloud Run built-in + custom counters. No custom metrics infrastructure needed.

### Error propagation

Cache failures never propagate to the client. The embedding service returns vectors regardless — from cache, from Voyage, or from Voyage after a cache failure. Error logging is internal.

### Security

| Boundary | Control |
|----------|---------|
| Embedding service → Embeddings bucket | Read-only IAM |
| Embedding service → Staging bucket | Write-only IAM |
| Eventarc → Writer | CloudEvent delivery via Pub/Sub push; authenticated via `eventarc.eventReceiver` on the writer SA |
| Writer → Staging bucket | Read + delete IAM |
| Writer → Embeddings bucket | Read + write IAM |
| Writer → Cloud Tasks (compaction) | Enqueue permission for compaction dispatch |
| Compaction → Embeddings bucket | Read + write IAM |
| Customer data isolation | Shard-per-source path convention. No cross-source queries. |

If the embedding service is compromised, the attacker can read cached embeddings (vectors, not source code) and write to staging. Staging files are eventually merged into the permanent bucket by the writer — so a compromised service can indirectly poison the cache with bad vectors. Mitigation: the writer validates parquet schema before merging, and poisoned vectors only degrade search quality (wrong approximate neighbors), they don't expose data or enable code execution. The 24h staging lifecycle limits the window for unmerged poison files.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| GCS Parquet + DuckDB httpfs | Dedicated vector database | No new infrastructure. DuckDB is embedded. Parquet is the lingua franca. Point queries on sorted files are fast enough. |
| Eventarc (GCS events) + separate writer | Direct writes to embeddings | IAM blast radius. Single-writer prevents corruption. At-least-once + sha256 dedup = clean semantics. Eventarc eliminates explicit dispatch code — the staging write is the trigger. |
| Content-only hash + model in path | Model in hash (like local cache) | Shard-level model migration. Old shards evict naturally. Cleaner GCS organization. |
| int8 native storage | float32 with quantization | Voyage produces int8 natively. No conversion, no quality loss. 4x smaller. |
| Inline cache layer | Decorator pattern (like local) | Need source identifier from gRPC request. Need pre-group access to chunks. |
| Cloud Run (scales to zero) | Always-on VM | Pay per request. Writer and compaction have bursty workloads. |
| Pulumi (C#) | Terraform (HCL) | Same language as the codebase. Strongly typed. Same tooling. |
| 6-month TTL eviction | No eviction / LRU | Embeddings are expensive. 6 months is generous. True LRU requires access-time tracking in immutable parquet — not worth the complexity. |

## Alternatives Considered

**Redis/Memcached in front of GCS:** Fast, but volatile. Cache loss means full recomputation. Embeddings are too expensive to lose. Parquet on GCS is persistent and cheap.

**BigQuery as cache store:** Serverless, scales infinitely. But query latency is 1-3 seconds (too slow for the hot path) and cost-per-query doesn't suit the high-frequency lookup pattern.

**Firestore/Cloud SQL:** Point lookups are fast, but storing 1KB vectors in a document/row store is expensive at scale and doesn't leverage columnar compression.

**Cache in the RepoQL host (extend local cache to cloud):** The local cache already handles per-machine and team sharing. A cloud layer would need HTTP transport, auth, and multi-tenant isolation — effectively building this service anyway but exposing it as a client feature rather than containing it in the service.

**No cache — just lower Voyage prices:** Possible if Voyage reduces pricing. But the cache also reduces latency (100ms vs 500ms+), which matters for interactive search. And the margin from cache hits is the business model.

## Risks

| Risk | Mitigation |
|------|------------|
| DuckDB httpfs latency on cold GCS files | Background prefetch when repo opened in UI. Object cache for warm files. |
| Single writer bottleneck at scale | Shard the writer by source prefix. Each writer instance handles a subset. |
| Eventarc delivery backlog | Monitor Pub/Sub subscription backlog. Scale writer instances. Events are small (GCS object metadata). |
| GCS cost surprises | Standard class for both buckets. 24h lifecycle on staging prevents accumulation. Monitor operation counts per shard. |
| Stale compaction lock | Lock uses GCS `ifGenerationMatch` preconditions. Stale after 1 hour → atomic overwrite via generation match. No races. |
| Customer discovers they can read other customers' embeddings via GCS | Vectors are not source code — they're lossy projections. Still, shard-per-source and IAM prevent enumeration. |

## Extension Points

- **Shard-level writer scaling** — assign writer instances to source prefixes when throughput demands it
- **Pre-computed cache distribution** — popular public repos cached once, served to all customers
- **Background prefetch** — warm GCS edge cache when a repo is opened, before queries arrive
- **Cache analytics** — which repos share the most content? Which customers benefit most?
- **Model migration tooling** — batch-recompute a shard under a new model during off-peak

---

## Related

- [North Star: Embedding Cache](../../north-star/embedding-cache.md) — what great looks like
- [Flows: Cloud Cache](../../flows/future/cloud-cache/) — embedding request, cache merge, compaction
- [Design: Local Embedding Cache](embedding-cache.md) — separate system, same principles
- [Design: LLM Service](llm-service.md) — the broader service this cache lives within
- [EmbeddingServiceImpl](../../../src/RepoQL.Embedding.Service/EmbeddingServiceImpl.cs) — where the cache integrates
