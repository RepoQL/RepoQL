# Proposal: Accurate Pipeline Status and Readiness (Simple)

## Problem
- `RepoQL.Core/RepositoryIndexer.cs:419` exposes `ClassificationQueueDepth` but returns the enrichment queue depth. Observers (CLI, health, tests) misread pipeline state, and the true classification backlog is hidden.
- gRPC `ReindexAll` (`RepoQL.ConsoleApp/Host/RepoQlServiceImpl.cs`) streams these depths as “Completed” and does not await writes, so progress appears inconsistent and backpressure/errors are lost.
- The indexer aggregates hashing, classification, parsing, enrichment scheduling, persistence integration, watcher fan‑out, metrics, and tracing. With only raw queue fields exposed, it’s hard to tell which stage is congested or whether the host is actually ready.

## Solution
Provide an accurate, minimal status surface that keeps the current monolithic indexer but exposes correct stage status, meaningful progress, and standard readiness signals.

1) Correct status (fix and clarify)
- Fix the miswired `ClassificationQueueDepth` to read the classification queue.
- Define stage status in terms of both backlog and work counters:
  - Stages: discovery/classification, parsing, analysis/enrichment.
  - Per stage, expose: `depth` (current backlog), `capacity`, `scheduled`, `completed` (monotonic counters).
- Add a single `PipelineSnapshot` read model on the indexer that returns these per‑stage values without leaking internal fields.

2) Meaningful progress
- Progress represents completed work, not current backlog. For each stage, `Completed` is the stage’s monotonic counter since a baseline captured at the start of an operation (e.g., reindex).
- gRPC streams progress derived from `PipelineSnapshot` and awaits `WriteAsync` to respect backpressure and surface errors. Throttle updates to reduce noise.
- Add an `IndexingMetrics` counter for enrichment (e.g., `StageEnrich`) to complement existing hash/parse counters; increment once per enriched document.

3) Readiness via gRPC Health
- Publish readiness using the standard `grpc.health.v1.Health` service with clear, stage‑oriented service names:
  - `repoql.discovery` → SERVING when classification queue is idle; NOT_SERVING when backlog > 0.
  - `repoql.parsing` → SERVING when parsing queue is idle; NOT_SERVING when backlog > 0.
  - `repoql.analysis` → SERVING when enrichment queue is idle; NOT_SERVING when backlog > 0.
  - `repoql.reindex` → NOT_SERVING while a reindex session is active; SERVING otherwise.
  - `repoql.ready` → SERVING only when discovery, parsing, and analysis are SERVING and the writer has flushed (best‑effort).
- Clients can efficiently wait for readiness using `Health/Watch` on any/all of the above services. For convenience, provide a `WaitForPipeline` RPC that waits for any or all stages to become idle, implemented with existing `WhenIdleAsync()` and writer flush.

4) Observability coherence
- The snapshot and health signals reflect the same queues and counters used internally, so dashboards, CLI, and gRPC consumers agree on which stage is active and when the host is ready. Tracing continues to link hash→classify→parse→enrich spans without refactoring.

This focuses on correctness and clarity without changing lifecycles or splitting the indexer. It replaces misleading depth‑only reporting with a small, reliable status surface and standard readiness semantics that clients can poll or watch.
