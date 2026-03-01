---
description: Plan for enhanced footer trust signal — cached summary, TrustSignal type, proto fields, formatting rules
tags: [diagnostics, footer, trust-signal, observability, plan]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: Trust Signal + Enhanced Footer

Implements: [Runtime Observability Design](../../../designs/future/runtime-observability.md) — Trust Signal, Footer Formatting, cached GetSummary

## Scope

**Covers:**
- Cached `UriRegistry.GetSummary()` with dirty-flag invalidation — recompute on next read after mutations, not on every mutation
- `TrustSignal` type with 8 fields, replacing/extending `IndexerStatus` (4 fields)
- Proto changes: 4 new fields on `QueryResponse` and `ExploreIndexerStatus` (`index_total`, `index_failed`, `index_stale`, `semantic_percent`)
- `FormatStatusFooter` updated with layered formatting rules (healthy compresses, degraded expands, failures appear only when non-zero)
- Host-side computation of TrustSignal from cached summary
- Client-side formatting of TrustSignal into footer string

**Does not cover:**
- `processing_queue()`, `failed_files()`, `system_health()` UDFs (Plan: [05-queue-observability](05-queue-observability.md))
- Queue commands (Plan: [06-queue-commands](06-queue-commands.md))
- Freshness: `last_scan_age_seconds` / file system watcher integration (future — the design notes this is the hardest layer)
- Parse depth: `parsed_percent` (future — requires format metadata not yet available)

## Enables

- Agent confirms trust in under 20 tokens on every response — north-star declaration
- Agent sees failures and stale counts without a separate diagnostic query — eliminates a diagnostic round-trip
- Plan 05 can use the cached `GetSummary()` for `system_health()` UDF — shared computation path
- Footer adapts to indexing progress: partial repos show percentages, complete repos compress to `ready`

## Prerequisites

None. All infrastructure exists:
- `IndexerStatus` in `src/RepoQL.Explore/IndexerStatus.cs` — 4-field record to extend
- `FormatStatusFooter` in `src/RepoQL.Explore/RepresentationFormatter.cs` — current formatting logic
- `UriRegistry` in `src/RepoQL.Contracts/UriRegistry/` — in-memory file status registry
- `ScopeReadiness` in `src/RepoQL.Contracts/UriRegistry/ScopeReadiness.cs` — already computes counts and percentages
- `repoql.proto` in `src/RepoQL.Protocol/Protos/repoql.proto` — gRPC contract

## North Star

The footer is 10-20 tokens that answer: "Can I trust these results?" Healthy signals disappear into a compact format. Degraded signals expand just enough to convey what's wrong. The agent never needs a separate query to assess trust.

## Done Criteria

### Cached GetSummary

- `UriRegistry` shall expose a `GetSummary()` method that returns counts by status: total, pending, failed, stale, indexed, embedded, embedding-applicable
- The summary shall be cached — computed once, invalidated on mutations (status changes, new registrations)
- Invalidation shall use a dirty flag: mutations set a flag, `GetSummary()` recomputes only when the flag is set
  - This prevents invalidation storms during rapid indexing (many mutations per second)
- Recomputation shall walk the registry once — O(n) per recompute, O(1) per read when clean
- A test shall verify the summary is correct after a series of status changes
- A test shall verify the cache returns the same instance when no mutations occur (no redundant recomputation)
- A test shall verify the cache is invalidated after a mutation

### TrustSignal Type

- A `TrustSignal` record shall be created with 8 fields:
  - `IndexTotal` (int) — total discovered files
  - `IndexPending` (int) — files not yet indexed
  - `IndexFailed` (int) — files in Failed status
  - `IndexStale` (int) — files modified since indexing
  - `SemanticEnabled` (bool) — embeddings feature on
  - `SemanticReady` (bool) — all embeddings complete
  - `SemanticPercent` (int) — percentage of applicable files embedded
  - `ExecutionTimeMs` (long) — query execution time
- `TrustSignal` shall be constructable from the cached summary + execution time
- `IndexerStatus` usage shall migrate to `TrustSignal` — `IndexerStatus` may be kept temporarily for backward compatibility or removed if all callers can be updated in this plan
- A test shall verify `TrustSignal` is correctly computed from a summary with known counts

### Proto Changes

- `QueryResponse` in `repoql.proto` shall gain: `int32 index_total`, `int32 index_failed`, `int32 index_stale`, `int32 semantic_percent`
- `ExploreIndexerStatus` shall gain the same 4 fields
- Existing fields shall remain unchanged. On `ExploreIndexerStatus`: `index_pending`, `semantic_ready`, `ready`, `elapsed_ms`. On `RawQueryResponse`: `execution_time_ms`, `index_pending`, `semantic_enabled`, `semantic_ready`
- Field numbers shall not conflict with existing fields
- The C# types wrapping `QueryResponse` and `ExploreResponse` shall expose the new fields through to callers
- A proto compilation shall succeed with no breaking changes

### Footer Formatting

- `FormatStatusFooter` shall implement the following rules:
  1. When `IndexPending == 0` and `IndexFailed == 0` and `IndexStale == 0`: show `index: ready`
  2. When `IndexPending > 0`: show `index: {percent}% ({pending} pending)` where percent = `(total - pending) / total * 100`
  3. When `IndexFailed > 0`: append `{failed} failed`
  4. When `IndexStale > 0`: append `stale: {stale}`
  5. When `SemanticEnabled` and `SemanticReady`: show `semantic: ready`
  6. When `SemanticEnabled` and not `SemanticReady`: show `semantic: {percent}%`
  7. When not `SemanticEnabled`: show `semantic: disabled`
  8. When `IndexTotal == IndexPending` and `IndexPending > 0`: show `NOT READY — {pending} pending, discovery in progress` (all discovered files are still pending — indexing hasn't made progress yet)
- A test shall verify healthy footer: `[{tok} | {ms} | index: ready | semantic: ready]`
- A test shall verify partial footer: percentage and pending count appear
- A test shall verify failures appear only when non-zero
- A test shall verify stale count appears only when non-zero
- A test shall verify NOT READY format when discovery is in progress
- A test shall verify the healthy footer stays under 20 tokens (count pipe-delimited segments)

### Host-Side Computation

- The host shall compute `TrustSignal` from `UriRegistry.GetSummary()` for each tool response
- The computation shall happen in the same code path that currently produces `IndexerStatus`
- The 4 new proto fields shall be populated on every `QueryResponse` and `ExploreResponse`
- When the cached summary is clean (no mutations since last read), the computation is O(1)

## Constraints

- **Footer under 20 tokens (common cases)** — the north-star budget. Worst case (all degraded) is ~24 tokens. Design: "acceptable as a trade against requiring a separate diagnostic query."
- **No new tables** — schema is frozen. TrustSignal is computed in-memory, not stored.
- **Backward-compatible proto** — new fields are additive. Old clients ignore them. New clients with old hosts see zeros, producing current footer behavior. Design constraint.
- **`IndexStale` uses existing `UriStatus.Stale`** — the UriRegistry already tracks files marked stale by the file system watcher. This plan exposes that existing count. The *deferred* freshness features are `last_scan_age_seconds` (time since last file system scan) and `parsed_percent` (format parse depth) — these require infrastructure not yet built (watcher integration, format metadata).

## References

- [Runtime Observability Design](../../../designs/future/runtime-observability.md) — Trust Signal, Footer Formatting, Cross-Cutting Concerns sections
- [Enhanced Footer Trust Signals Flow](../../../flows/future/diagnostics/footer-trust-signals.md) — formatting rules and token budget analysis
- `src/RepoQL.Explore/IndexerStatus.cs` — current 4-field status
- `src/RepoQL.Explore/RepresentationFormatter.cs` — `FormatStatusFooter` at line ~225
- `src/RepoQL.Contracts/UriRegistry/ScopeReadiness.cs` — existing percentage computation
- `src/RepoQL.Protocol/Protos/repoql.proto` — gRPC contract
- `docs/knowledge/testing-guidelines.md` — TUnit, AwesomeAssertions

## Error Policy

The footer must never fail. If `GetSummary()` throws (should not happen for in-memory data), degrade to current behavior — show the existing 4-field `IndexerStatus`. A silent degradation is better than a tool response failure caused by footer computation.
