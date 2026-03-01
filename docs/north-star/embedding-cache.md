# Embedding Cache: What Great Looks Like

> Compute an embedding once. Use it everywhere, forever — until the content or the model changes.

A team imports `github://company/platform-docs` in every repository they work on. The first developer pays the embedding cost — a few minutes for a large corpus. Every developer after that pays nothing. The embeddings are already there, because the content is the same and the model is the same. When the docs are updated, only the changed pages re-embed. When the model is upgraded, the cache naturally misses and rebuilds — no purge command, no manual intervention. An agent working across three repositories never embeds the same utility library twice. A company shares a cache directory so new hires have instant semantic search on day one. Eventually, popular public repositories come with embeddings pre-computed — the agent imports TypeScript's source and search works immediately.

---

## Content Identity

- An agent should be able to import the same content in two repositories and pay the embedding cost only once
- An agent should be able to re-index an unchanged file without recomputing its embedding
- An agent should be able to trust that two identical texts always produce the same cache key, regardless of file path, repository, or machine
- An agent should be able to upgrade the embedding model and have stale entries miss naturally — no purge, no manual invalidation

```
Same content + same model → same key → cache hit
Changed content           → different key → cache miss → recompute
Changed model             → different key → cache miss → recompute
```

---

## Dimensional Flexibility

- An agent should be able to store an embedding once and use it at any supported dimensionality
- An agent should be able to change the operational dimension without recomputing anything
- An agent should be able to use a cache populated by a team running 768 dimensions even if they only need 384

```
Stored: 768-dimensional vector (full model output)
Used:   first 384 → re-normalize → valid 384-dim embedding
        first 256 → re-normalize → valid 256-dim embedding
```

---

## Cross-Boundary Sharing

- An agent should be able to benefit from embeddings computed in a different repository on the same machine
- A team should be able to share a pre-computed embedding cache without coordination beyond a shared path
- An organization should be able to publish embedding caches that any member can use read-only
- An agent should be able to use a shared cache without write access to it

```
Developer A:  imports company/docs → computes → writes to shared cache
Developer B:  imports company/docs → cache hit → instant search
Developer C:  new hire → points at shared cache → semantic search from minute one
```

---

## Layered Resolution

- An agent should be able to read from multiple cache sources with clear priority
- An agent should be able to fall through from local to shared to remote without explicit configuration per query
- An agent should be able to add or remove cache layers without affecting correctness
- An agent should be able to write only to the local layer while reading from all layers

---

## Trust and Integrity

- An agent should be able to verify a cached embedding by recomputing it from the same input
- An agent should never silently use an embedding from an incompatible model
- An agent should be able to see cache hit rates and know whether the cache is helping
- An agent should be able to function with no cache, a cold cache, or a fully warm cache — the only difference is speed

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Same content + same model = cache hit, always | Deterministic. No false misses, no stale hits. |
| Store full dimensions, truncate on read | One entry serves all consumers regardless of dim config |
| Cross-repo sharing with zero coordination | Import a popular library and skip hours of embedding |
| Layered caches compose naturally | Local speed + shared breadth + cloud reach, same interface |
| No cache is also fine | Cache is pure acceleration, never correctness-critical |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Fingerprint by file path or timestamp | An agent should be able to get cache hits for identical content at different paths |
| Store per-dimension entries | An agent should be able to store once and truncate to any dimension |
| Require write access to shared caches | An agent should be able to benefit from a read-only shared cache |
| Make search depend on cache availability | An agent should get correct results with or without a cache |
| Require manual invalidation on model change | An agent should see natural misses when the model changes |
| Conflate passage and query embeddings in the cache | An agent should be able to trust that passage and query embeddings are cached separately (asymmetric models produce different vectors for the same text) |

---

*An agent should never compute an embedding that someone — on this machine, on this team, or in this community — already computed for the same content.*
