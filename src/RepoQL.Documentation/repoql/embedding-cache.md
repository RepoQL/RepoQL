---
description: "Local parquet-backed embedding cache for cross-repo reuse and faster re-indexing."
tags: ["embeddings", "cache", "parquet", "configuration", "performance"]
audience: ["LLMs", "Developers"]
categories: ["Reference[100%]"]
---

# Embedding Cache

RepoQL can cache computed embeddings on disk so repeated content does not need to be re-embedded.

The cache is:
- Content-addressed (`SHA256(model + "\0" + type + "\0" + text)`)
- Local to your machine by default
- Automatic (hit when possible, compute on miss)
- Acceleration-only (cache failures never block embedding)

## Configuration

Settings are under `embedding.cache`:

```json
{
  "embedding": {
    "cache": {
      "enabled": true,
      "path": "~/.repoql/embedding-cache/",
      "paths": ["~/.repoql/embedding-cache/", "//server/share/embeddings/"],
      "compaction_threshold": 100,
      "max_size_mb": 500
    }
  }
}
```

Key fields:
- `embedding.cache.enabled`: enable/disable cache usage (default `true`)
- `embedding.cache.path`: local parquet directory (default `~/.repoql/embedding-cache/`)
- `embedding.cache.paths`: ordered list of cache directories (first is local read-write, rest are shared read-only)
- `embedding.cache.compaction_threshold`: compact when parquet file count exceeds this threshold (default `100`)
- `embedding.cache.max_size_mb`: target max cache size after compaction (default `500`, `0` means unlimited)

When `paths` is set, it takes precedence over `path`. The first entry is the local write target; remaining entries are read-only shared caches.

## How It Works

For each batch:
1. Compute deterministic hash keys for each input text.
2. Lookup hashes in local parquet cache files.
3. Send only misses to the inner embedding provider.
4. Write newly computed vectors back to cache as a new parquet file.
5. Return merged results in original order.

Passage and query embeddings are isolated by hash type (`"p"` vs `"q"`), so identical text can cache both forms safely.

## Verify It Is Working

Look for per-batch log lines like:

```
Embedding cache: 80/100 hits (80.0%), 20 to compute
```

A warm cache should show:
- Higher hit percentages over time
- Fewer provider calls for repeated indexing
- Faster embedding stages on unchanged content

## Maintenance

Cache maintenance is best-effort and fully non-blocking for embedding operations.

- **Compaction trigger**: after write-back, if parquet file count is greater than `compaction_threshold`, RepoQL starts background compaction.
- **Startup trigger**: RepoQL also starts a background compaction attempt during host startup.
- **Cross-process lock**: compaction uses `/.compaction.lock` in the cache directory. The lock contains `pid` and `timestamp` JSON.
- **Stale lock recovery**: if a lockfile points to a dead process, RepoQL removes it and continues.
- **Merge + dedupe**: compaction reads all parquet shards, keeps the newest row per `text_hash` (by `created_at`), and writes one merged shard atomically (temp file then rename).
- **Eviction**: if `max_size_mb > 0`, compaction evicts oldest rows (`created_at`) until the merged parquet fits the size target.
- **Cleanup behavior**: old shard deletion is best effort; files with open handles (common on Windows) are skipped and cleaned up on later compaction runs.

Any maintenance failure is logged as a warning and does not block reads or writes.

## Shared Caches

Teams can share pre-computed embeddings via a shared directory. Each developer reads from the shared cache; new embeddings are written to the local cache only.

### Setup

1. One developer (or CI) populates the shared cache by copying their local cache directory to a shared location:

```bash
cp -r ~/.repoql/embedding-cache/ //server/share/repoql-embeddings/
```

2. Each developer adds the shared path to their config:

```json
{
  "embedding": {
    "cache": {
      "paths": [
        "~/.repoql/embedding-cache/",
        "//server/share/repoql-embeddings/"
      ]
    }
  }
}
```

### How It Works

- Lookups check paths in order. The first hit wins per hash.
- Hits from shared paths are automatically written through to the local cache, so subsequent lookups are local-speed.
- The shared path only needs read permissions. The local path needs read-write.
- If a shared path is unreachable (network error, missing directory), it is skipped silently. The cache falls back to local-only behavior.
- Compaction and eviction only apply to the local (first) path. The shared cache owner manages their own maintenance.
