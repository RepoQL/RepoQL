# Cloud Embedding Cache Plans

Implementation plans for the cloud embedding cache — avoiding redundant Voyage API calls across customers who share common code.

## Overview

These plans implement the [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md). The cache lives inside the existing embedding service and stores vectors in GCS parquet files, partitioned by source repo and model.

## Dependency Order

```
┌─────────────────────┐     ┌──────────────────────┐
│  01-infrastructure  │     │  02-proto-source      │
│  Pulumi, GCS, IAM   │     │  Proto field, normalize│
└──────────┬──────────┘     └──────────┬────────────┘
           │                           │
           ├───────────┬───────────────┤
           │           │               │
           ▼           ▼               │
┌────────────────┐  ┌────────────────┐ │
│ 04-writer      │  │ 03-cache-layer │◄┘
│ Eventarc       │  │ DuckDB, lookup │
│ consumer       │  │ staging write  │
└────────┬───────┘  └────────────────┘
         │
         ▼
┌────────────────┐
│ 05-compaction  │
│ Dedup, evict   │
│ consolidate    │
└────────────────┘
```

## Plans

| # | Plan | What it delivers |
|---|------|------------------|
| 01 | [Infrastructure](01-infrastructure.md) | Pulumi stacks, GCS buckets, IAM, Cloud Scheduler, Eventarc IAM grants |
| 02 | [Proto & Source Resolution](02-proto-source-resolution.md) | `source` proto field, URL normalization, host-side resolution |
| 03 | [Cache Layer](03-cache-layer.md) | `EmbeddingCacheLayer` — DuckDB lookup, staging write (Eventarc triggers writer) |
| 04 | [Writer Service](04-writer-service.md) | Cloud Run writer — Eventarc consumer, part file append, `_source.json` |
| 05 | [Compaction](05-compaction.md) | Cloud Run job — shard locking, dedup, eviction, part consolidation |

## Execution Strategy

**Phase 1: Foundation (01 + 02)** — can proceed in parallel
- Infrastructure creates the buckets, IAM, and Eventarc IAM grants
- Proto change adds the `source` field and normalization

**Phase 2: Core (03 + 04)** — can proceed in parallel, both depend on 01
- Cache layer is the hot path (lookup + write-back)
- Writer processes staging files into permanent shards

**Phase 3: Scale (05)** — depends on 04
- Compaction keeps shards performant and bounded

## Success Criteria

When complete:

```
RepoQL Host → EmbedChunks(groups, source="github.com/org/repo")
                    │
                    ▼
           ┌─ Cache hit?  → Return cached vectors (100-150ms)
           └─ Cache miss? → Voyage → Return + stage for write-behind (600-2200ms)
                                          │
                                          ▼
                              Writer appends to shard → Compaction consolidates
```

- Cache hit rate climbs over time for active repos
- Voyage API calls reduced proportional to shared code
- Cache self-manages size via 6-month TTL eviction
- Model upgrade invalidates naturally via shard-level key mismatch

## Related

- [Cloud Embedding Cache Design](../../../designs/future/cloud-embedding-cache.md) — architecture and contracts
- [Cloud Cache Flows](../../../flows/future/cloud-cache/) — embedding request, cache merge, compaction
- [North Star: Embedding Cache](../../../north-star/embedding-cache.md) — what great looks like
- [Local Embedding Cache Plans](../embedding-cache/) — separate system, same principles
