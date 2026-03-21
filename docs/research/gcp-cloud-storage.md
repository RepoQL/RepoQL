# Google Cloud Storage

Research for object storage patterns with GCS.

*Research date: March 19, 2026*

## Context

RepoQL uses two buckets: `repoql-embeddings-{env}` (permanent Parquet embedding cache, Standard class, US multi-region) and `repoql-staging-{env}` (temporary staging files, 1-day lifecycle deletion). Access via both native GCS SDK (`Google.Cloud.Storage.V1`) and HMAC keys (S3-compatible API for DuckDB httpfs).

---

## Storage Classes

| Class | Min Duration | Retrieval Fee | Use Case |
|-------|-------------|---------------|----------|
| Standard | None | None | Frequently accessed data |
| Nearline | 30 days | $0.01/GB | ~once/month access |
| Coldline | 90 days | $0.02/GB | ~once/quarter |
| Archive | 365 days | $0.05/GB | <once/year |

Early deletion fees apply if objects are removed before minimum duration.

**Autoclass**: Automatically transitions between classes based on access patterns. Objects must be ≥128 KiB. Management fee applies but retrieval/early deletion waived.

RepoQL's Standard class is correct — embeddings are frequently read, staging is consumed within hours.

> [Storage classes](https://docs.cloud.google.com/storage/docs/storage-classes)

---

## Locations

| Type | Characteristics |
|------|----------------|
| Region (e.g., `us-central1`) | Lowest cost, best co-located perf |
| Dual-Region | Predictable geo-redundancy, turbo replication option |
| Multi-Region (`US`, `EU`, `ASIA`) | Broadest availability, highest cost |

RepoQL uses US multi-region. Since Cloud Run is `us-central1`, a regional bucket would be cheaper ($0.020 vs $0.026/GB/month storage, $0.05 vs $0.10/10K Class A ops) with no perf penalty — tradeoff is losing geo-redundancy.

> [Bucket locations](https://docs.cloud.google.com/storage/docs/locations)

---

## Access Control

### IAM vs ACLs

IAM preferred. **Uniform bucket-level access** disables ACLs entirely (RepoQL correctly enables this).

### Key Roles Used by RepoQL

| Role | Used By | Scope |
|------|---------|-------|
| `objectViewer` | Embedding service, Cloud service | Embeddings bucket |
| `objectCreator` | Embedding service, Cloud service | Staging bucket |
| `objectAdmin` | Cache writer (both), Compaction (embeddings) | Per-bucket |

### HMAC Keys

Enable S3-compatible API access for DuckDB httpfs. 61-char access ID + 40-char Base64 secret per service account (max 10 keys/SA). Activation delay ~60 seconds.

> [Access control overview](https://docs.cloud.google.com/storage/docs/access-control)
> [HMAC keys](https://docs.cloud.google.com/storage/docs/authentication/hmackeys)

---

## Lifecycle Management

### Actions

| Action | Effect |
|--------|--------|
| Delete | Permanently remove object |
| SetStorageClass | Change class |
| AbortIncompleteMultipartUpload | Clean up incomplete uploads |

### Common Conditions

`Age`, `CreatedBefore`, `IsLive`, `MatchesStorageClass`, `MatchesPrefix`, `MatchesSuffix`, `NumberOfNewerVersions`.

Delete takes precedence over SetStorageClass when both match.

RepoQL: staging bucket has `Age=1` deletion. Embeddings bucket has no lifecycle rules (compaction handles eviction).

> [Object Lifecycle Management](https://docs.cloud.google.com/storage/docs/lifecycle)

---

## Performance

### Request Rate Limits

| Metric | Initial Capacity |
|--------|-----------------|
| Write QPS (per bucket) | ~1,000 objects/sec |
| Read QPS (per bucket) | ~5,000 objects/sec |
| Same-object writes | 1/second |

Ramp up gradually: double no faster than every 20 minutes. GCS auto-redistributes hotspots.

### Upload Types

| Type | When |
|------|------|
| Simple | <16 MiB |
| Resumable | >16 MiB or unreliable network |
| Parallel composite | Large objects, fast networks (up to 32 chunks) |

### Key Limits

| Resource | Limit |
|----------|-------|
| Max object size | 5 TiB |
| Pub/Sub notifications per bucket | 100 total; 10 per event type |
| Custom metadata per object | 8 KiB |

### Naming for Performance

Avoid sequential prefixes (timestamps). RepoQL uses UUID-based staging names and SHA256-prefixed embedding paths — both provide excellent distribution.

> [Request rate](https://docs.cloud.google.com/storage/docs/request-rate)
> [Quotas](https://docs.cloud.google.com/storage/quotas)

---

## Pricing (US multi-region, Standard)

| Item | Cost |
|------|------|
| Storage | $0.026/GB/month |
| Class A ops (writes, lists) | $0.10/10,000 |
| Class B ops (reads) | $0.004/10,000 |
| Retrieval | Free (Standard) |
| Same-region egress | Free |
| Cross-continent egress | $0.12/GB (first 1 TB) |

Regional would be cheaper: $0.020/GB storage, $0.05/10K Class A ops.

> [Storage pricing](https://cloud.google.com/storage/pricing)

---

## Consistency Model

**Strong global consistency** for all operations:

| Operation | Consistency |
|-----------|-------------|
| Read-after-write | Strongly consistent |
| Read-after-delete | Strongly consistent (immediate 404) |
| Object/bucket listing | Strongly consistent |

Exceptions: cached public objects (eventual, ~60 min), IAM propagation (~1 min).

For multi-region: metadata synced synchronously, data replicated asynchronously. Reads before replication → retryable 500 (never stale data).

RepoQL relies on strong consistency for `UploadWithPreconditionAsync` (generation match) and immediate visibility of composed Parquet files.

> [Consistency](https://docs.cloud.google.com/storage/docs/consistency)

---

## Notifications

GCS → Pub/Sub notifications for object events. Eventarc wraps these into CloudEvents for Cloud Run.

- At-least-once delivery
- Typically arrives within seconds
- 10 notification configs per event type per bucket
- 100 total per bucket

> [Pub/Sub notifications](https://docs.cloud.google.com/storage/docs/pubsub-notifications)

---

## .NET SDK (`Google.Cloud.Storage.V1`)

### Key Methods

| Operation | Method |
|-----------|--------|
| Upload | `UploadObjectAsync(bucket, name, contentType, stream, options)` |
| Download | `DownloadObjectAsync(bucket, name, stream, options)` |
| List | `ListObjectsAsync(bucket, prefix, options)` — auto-paginates |
| Delete | `DeleteObjectAsync(bucket, name, options)` |

### Preconditions

```csharp
// Create only if not exists
await client.UploadObjectAsync(bucket, name, type, stream,
    new UploadObjectOptions { IfGenerationMatch = 0 });

// Optimistic concurrency
await client.UploadObjectAsync(bucket, name, type, stream,
    new UploadObjectOptions { IfGenerationMatch = knownGeneration });
```

RepoQL wraps `StorageClient` behind `IObjectStorageClient` with `S3ObjectStorageClient` for local dev (MinIO via Aspire).

> [StorageClient reference](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Storage.V1/latest)
> [Request preconditions](https://docs.cloud.google.com/storage/docs/request-preconditions)

---

## Security

| Layer | Detail |
|-------|--------|
| Default encryption | AES-256, automatic, free |
| CMEK | Customer-managed via Cloud KMS |
| CSEK | Customer-supplied per-request |
| VPC Service Controls | Perimeter around GCS |
| Uniform bucket-level access | IAM only, no ACLs |

RepoQL uses default encryption — adequate for embedding vectors (not sensitive, deterministic, recomputable).

> [Data encryption](https://docs.cloud.google.com/storage/docs/encryption)

---

## Parquet on GCS

| Parameter | Recommendation |
|-----------|---------------|
| Row group size | ≥16 MiB |
| Compression | Snappy or ZSTD |
| Column types | Native (not strings) — enables predicate pushdown |

DuckDB httpfs reads Parquet via S3-compatible API, caches footers, uses predicate pushdown on `sha256` for minimal data transfer.

---

## Gaps

- **Multi-region vs regional cost comparison**: Regional would save ~23% on storage with no performance penalty since all access is from `us-central1`
- **Soft delete pricing**: Recently GA; retention period costs not investigated
- **DuckDB httpfs caching behavior**: DuckDB-side, not GCS-documented
- **Rapid storage class**: Newer zonal-only class, not widely documented

---

## Summary

| Topic | Key Takeaway |
|-------|-------------|
| Buckets | Embeddings (permanent Parquet) + Staging (1-day lifecycle) |
| Access | IAM + HMAC keys for S3-compatible DuckDB access |
| Consistency | Strongly consistent — critical for precondition-based writes |
| Pricing | $0.026/GB/month multi-region Standard; regional would be cheaper |
| Performance | Good key distribution via SHA256/UUID prefixes |
| Notifications | 10 per event type per bucket — key constraint for Eventarc |
