# Proposal: Introduce a gRPC Application Layer

## Problem
- The service implementation handles transport, orchestration, and persistence in one place. `src/RepoQL.ConsoleApp/Host/RepoQlServiceImpl.cs:27-60` materializes query results, duplicates cursor logic, and replays the same SQL a second time to decide whether the result set is truncated, doubling the load on DuckDB.
- The same file streams reindex progress by issuing `responseStream.WriteAsync` without awaiting (`src/RepoQL.ConsoleApp/Host/RepoQlServiceImpl.cs:331-336`), so write failures are silently discarded and back-pressure cannot propagate.
- Business rules (URI canonicalization, summary shaping, lease tracking) live side-by-side with gRPC plumbing, which makes it difficult to share the logic with other front-ends or to test it outside of the gRPC surface.
- The host cannot apply cross-cutting policies such as timeouts, retries, or logging in a consistent manner because every method implements bespoke flow control.

## Solution
- Introduce an application/service layer (`QueryExecutor`, `SummaryService`, `ReindexCoordinator`, etc.) living under `RepoQL.Core` or a dedicated assembly. Each gRPC method delegates to the service layer, keeping transport code focused on parsing the request and shaping the response.
- Move stream handling into the coordinator so it awaits progress writes and handles cancellation and errors coherently. The coordinator can consume accurate depth data from the pipeline (see `docs/proposals/indexer-pipeline-stages.md`) and decide when to emit progress updates.
- Consolidate query execution into a reusable component that handles column metadata, row shaping, and truncation detection without re-running the SQL, eliminating the redundant `store.RawQuery` call.
- With a clean separation, the same services can back future HTTP or CLI entry points, unit tests can exercise domain behavior without spinning up gRPC, and the transport layer becomes a thin adapter rather than a monolith.

