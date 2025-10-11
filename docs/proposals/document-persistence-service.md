# Proposal: Centralize Document Persistence

## Problem
- Both the indexer and the writer re-implement “replace document” persistence. `src/RepoQL.Core/RepositoryIndexer.cs:74-155` performs artifact upserts, remaps child nodes, rewrites spans and edges, and replaces document content directly on the `IGraphStore`.
- The hosted writer repeats the same work in `src/RepoQL.Data.DuckDB/SingleThreadedDatabaseWriter.cs:211-296`, adding front-matter enrichment and its own shape of node remapping. The two implementations already diverge (front-matter handling only exists in the writer path), which is a maintenance hazard.
- Any schema evolution now requires updating both code paths, and subtle bugs (for example mismatched artifact IDs or missing edge scope updates) arise when one path is modified without the other.
- Because both implementations live on the hot path, the duplication also makes it harder to instrument or swap out persistence behavior: there is no single place to hook audit logging, caching, or transactional safeguards.

## Solution
- Extract a dedicated `DocumentPersistenceService` (or similar) that encapsulates the replace logic: artifact upsert, document normalization, child node remap, span/edge rewrite, and optional property enrichment.
- Have both the indexer’s “direct write” fast path and the single-threaded writer call into that service; the writer would continue to own transactional ordering, while the indexer reuses the same persistence contract when bypassing the queue.
- Keep the front-matter projection and any future normalization inside the persistence service so there is a single definition of how records are translated into the database representation.
- With one shared implementation we reduce drift, make schema changes safer, and provide a clear extension point for future persistence policies (e.g., batched writes, alternative stores).

