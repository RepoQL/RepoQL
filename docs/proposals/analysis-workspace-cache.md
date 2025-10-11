# Proposal: Digest-Checked Workspace Cache (Simple)

## Problem
- `src/RepoQL.Core/AnalysisWorkspace.cs:21-63` caches `DocumentModel` by URI only. Once loaded, `LoadAsync` keeps returning the same instance even if the file changes.
- The indexer’s own cache does not help callers that use `IAnalysisWorkspace` directly (on-demand analysis, templating, API endpoints), so they can observe stale text, metadata, and syntax trees.
- Deletions are sticky because the workspace caches `null` for missing files; re-adding a file keeps returning `null` until restart.
- Callers cannot easily validate freshness because there is no standard digest attached to the returned model.

## Goal (bias towards simplicity)
- Guarantee that `LoadAsync` never returns stale content, without adding new public APIs or cross-component coordination.
- Avoid complex multi-version caches, external invalidation, or significant refactors.

## Minimal Solution
Keep the cache keyed by URI, but validate freshness by digest before serving from cache.

- On `LoadAsync(uri)`:
  1) Compute the file’s digest using `IHasher` (already done today).
  2) If the cache contains an entry for `uri` with the same digest, return it.
  3) Otherwise, (re)load the document, then store it in the cache alongside the digest.
  4) If the file does not exist, remove any existing cache entry and return `null` (do not cache `null`).

- Attach a standard digest to the returned `DocumentModel` via metadata, e.g. `"repoql.digest"`, so callers can read it uniformly regardless of format.

This approach:
- Eliminates stale reads with minimal code changes.
- Removes sticky deletion behavior by not caching `null`.
- Requires no API changes and no indexer/workspace invalidation wiring.

## Implementation Sketch
- Change the workspace cache value to store both the `DocumentModel` and its digest (e.g., a small record/class).
- In `AnalysisWorkspace.LoadAsync`:
  - Compute digest first.
  - Check the cache entry for the same digest; if mismatched or absent, load fresh and replace the entry.
  - When loading, ensure the returned `DocumentModel` includes `"repoql.digest"` in `Metadata` (set by the workspace if the loader didn’t already add a digest).
  - If the file is missing, remove any cache entry and return `null` without inserting a cache entry.
- Optional but trivial: a coarse per-URI lock to avoid duplicate reloads on concurrent calls.

## Trade-offs
- We still pay one hash read per `LoadAsync` call (as today). We avoid any extra reads beyond that.
- There is a small race if a file changes between the hash and the subsequent load; a later call will self-correct. Handling this precisely would complicate the design and is out of scope for the simple fix.
- We keep only one document per URI (latest digest), avoiding multi-version cache complexity or eviction policies.

## Non-goals (deferred for later proposals)
- Cross-component invalidation between indexer and workspace.
- Multi-version `(uri, digest)` caches or global LRU policies.
- API surface changes (e.g., new overloads returning `(Document, Digest)` or cache management methods).

## Tests (happy-path and regressions)
- Updating a file causes `LoadAsync` to return the new content without restart.
- Deleting a file removes the cache entry; re-adding the file returns a non-null document.
- If a loader did not set a digest, `DocumentModel.Metadata["repoql.digest"]` is present and matches the computed digest.
