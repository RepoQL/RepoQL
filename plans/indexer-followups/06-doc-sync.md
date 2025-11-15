# Task 6: Sync Architectural Docs with Indexer Changes

## Why
We have added new phases, host behavior, and DuckDB schema updates, but the living documents (`docs/STATE-MACHINE.md`, `docs/pipeline.md`, DuckDB schema docs) still reference the legacy RepositoryIndexer and FK layout. Keeping documentation current is critical before fully deprecating the old stack.

## Plan
1. Audit the following documents:
   - `src/Indexing/RepoQL.Indexing/docs/STATE-MACHINE.md`
   - `src/Indexing/RepoQL.Indexing/docs/pipeline.md`
   - `src/RepoQL.Data.DuckDB/docs/Schema.md` (or equivalent)
2. Update terminology from `RepositoryIndexer` to `RepoqlHost + IndexingCoordinator + IndexingEngine`.
3. Document the watcher backpressure behavior, per-stage counters, and shutdown semantics once implemented.
4. Update schema diagrams/text to reflect current constraints (e.g., no FK on `document_embedding`, behavior when deleting documents).
5. Add a short “operability” section describing new logging (info per phase, trace per processor).

## Pseudocode / Structure
- Treat each doc as a focused edit; no code changes required.
- Include diagrams or tables where useful (stage counters, queue states).

## Definition of Done
- Docs mentioned above accurately reflect the new design and constraints.
- No references remain to `RepositoryIndexer` as the preferred path.
- Schema documentation matches the actual SQL deployed at runtime.
