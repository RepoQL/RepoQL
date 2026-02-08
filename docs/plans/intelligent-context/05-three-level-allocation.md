---
description: Plan for three-level budget allocation — cluster to file to object/snippet
tags: [explore, allocation, budget, clusters, representation]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Three-Level Allocation

Implements: [Intelligent Context Design](../../designs/future/intelligent-context.md) — Cluster-Aware Allocation section

## Scope

**Covers:**
- `ClusterDecision` record
- `ValueBasedDecisionEngine.AllocateWithClusters` method
- Level 1: cluster-level budget distribution (proportional to cluster EV)
- Level 2: file-level allocation within cluster (delegates to existing `Allocate`)
- Level 3: object-level allocation within file (existing, enhanced with Focused)
- Cluster pressure relief (drop lowest-EV clusters when over-budget)
- Duplicate EV demotion (0.5× for duplicates in file-level allocation)
- `ExploreOrchestrator` integration (pass clusters to allocator)

**Does not cover:**
- Focused representation enum, estimation, rendering (Plan: 01-focused-snippets) — consumed here
- Query expansion (Plan: 02-query-expansion)
- SimHash dedup and duplicate detection (Plan: 03-simhash-dedup) — consumed here
- Clustering logic (Plan: 04-clustered-output) — consumed here

## Enables

Once Three-Level Allocation exists:
- **Breadth protected** — one high-scoring cluster can't starve others
- **Duplicates cost less** — demoted EV means duplicates get Compact; recovered tokens go to non-duplicates
- **All 5 phases compose** — the full pipeline works: expand → search → dedup → cluster → allocate → render

## Prerequisites

- `ResultCluster` and `IResultClusterer` from Plan 04 (clustered output)
- `Representation.Focused` and `ExploreTokenEstimator.EstimateFocused` from Plan 01 (focused snippets)
- `ExploreResult.DuplicateOf` from Plan 03 (simhash dedup) — or null when Plan 03 not deployed
- Existing `ValueBasedDecisionEngine.Allocate` method operational

## North Star

Budget flows through clusters like water through a watershed — each basin gets its share, then distributes internally. A 3000-token budget across 3 clusters gives each cluster enough to show its best content, instead of one dominant file consuming everything.

## Done Criteria

### ClusterDecision

- `ClusterDecision` shall include: `Cluster` (ResultCluster), `AllocatedBudget` (int), `FileDecisions` (IReadOnlyList<RenderingDecision>), `OmittedFileCount` (int)

### AllocateWithClusters

- `ValueBasedDecisionEngine` shall expose `IReadOnlyList<ClusterDecision> AllocateWithClusters(IReadOnlyList<ResultCluster> clusters, Intent intent, int tokenBudget)`
- The method shall not modify existing `Allocate` behavior — it is a new method alongside it

### Level 1: Cluster Budget Distribution

- The allocator shall reserve budget for fixed costs before distribution:
  - Cluster header overhead: ~10 tokens per non-Ungrouped cluster
  - Status footer: ~30 tokens
  - Expansion annotation: ~15 tokens (when present)
- Cluster EV shall be `max(member.Confidence) × intentModifier × (1 + 0.1 × log2(clusterSize))`
  - Intent modifiers: Inspect=1.2, Locate=1.0, Inventory=0.8, Explain=1.1
  - `log2(1) = 0`, so single-member clusters get no size bonus
- Each cluster's budget shall be `distributableBudget × (clusterEV / sum(allClusterEV))`
- Every cluster shall receive at minimum enough budget for its highest-scoring result at Compact level (~50 tokens)
  - If a cluster cannot afford even Compact for one result, drop the cluster entirely

### Cluster Pressure Relief

- While the sum of minimum cluster costs exceeds distributable budget, drop the cluster with the lowest EV
- Redistribute dropped cluster's budget proportionally among surviving clusters
- Dropped clusters shall appear in a truncation notice: `[More: {count} clusters ({labels}) with {fileCount} files]`

### Level 2: File Allocation Within Cluster

- For each surviving cluster, run the utility model on the cluster's `ExploreResult` members with the cluster's allocated budget
- `AllocateWithClusters` carries its own per-cluster allocation logic rather than delegating to the existing `Allocate(IReadOnlyList<SearchResult>)`, because cluster members are `ExploreResult` (not `SearchResult`). The utility inputs (Confidence, Kind, SemanticType, child objects) are all available on `ExploreResult`
- The utility formula (`P_relevance × V(option, intent) × evidenceQuality × novelty`) runs unchanged within each cluster's scope

### Duplicate EV Demotion

- When a file has `DuplicateOf` set (non-null), its confidence shall be halved before computing EV in Level 2
  - This naturally demotes duplicates to Compact or Minimal without special-casing
  - Recovered budget flows to non-duplicate files in the same cluster
- When `DuplicateOf` is null on all results (Plan 03 not deployed), no demotion occurs

### Level 3: Object Allocation Within File

- The existing within-file allocation logic runs unchanged
- The PickBestFit progression shall include Focused (from Plan 01):
  - Rich → Focused → Standard → Compact → Minimal
  - When Plan 01 is not deployed, Focused is skipped (EstimateFocused returns int.MaxValue)

### Orchestrator Integration

- `ExploreOrchestrator` shall pass `ResultCluster[]` to `AllocateWithClusters` when clusters are available
- When clusters are not available (Plan 04 not deployed), wrap each ExploreResult in a single-member Ungrouped cluster and pass to `AllocateWithClusters`
  - This degrades to the existing flat allocation behavior

### Novelty Tracking

- `NoveltyTracker` shall run per-cluster, not globally across all clusters
- This is intentional: cross-cluster novelty created the problem this plan solves (one dominant type starving other areas). Per-cluster novelty lets each cluster's content compete fairly within its own budget
- A cluster of 4 C# classes gets fresh novelty tracking — the 4th class is not penalized because unrelated clusters also contained C# classes

### Budget Minimum

- When `tokenBudget < 500`, skip cluster-level allocation entirely — treat all results as a single flat list and delegate to the existing file-level allocation logic
- This avoids excessive cluster header overhead at low budgets

### Budget Invariant

- `sum(all rendered tokens across all clusters) <= totalBudget`
- Budget utilization (tokens spent / budget) shall be >= 0.90 for budgets >= 1000

### Passthrough

- When all clusters are single-member Ungrouped (Plan 04 not deployed, or 1 result), `AllocateWithClusters` shall produce output equivalent to the existing `Allocate` method
- When no duplicates exist (Plan 03 not deployed), no EV demotion occurs
- When no Focused representation available (Plan 01 not deployed), Focused is skipped in PickBestFit

## Constraints

- **Extend, not replace** — `AllocateWithClusters` is a new method; existing `Allocate` is unchanged and still callable
- **Cluster level is thin** — it distributes budget and delegates to existing `Allocate` per cluster; no new utility model
- **Intent modifiers match design** — Inspect concentrates, Inventory flattens; match the design table exactly
- **Minimum 6 clusters before merging** — if > 6 clusters exist, merge smallest Ungrouped clusters into a single "Other" cluster to prevent excessive headers

## References

- [Intelligent Context Design](../../designs/future/intelligent-context.md) — Cluster-Aware Allocation, extension strategy, cluster EV formula
- [Budget Allocation Flow](../../flows/future/intelligent-context/budget-allocation.md) — three-level flow with example
- `src/RepoQL.Explore/ValueBasedDecisionEngine.cs` — engine to extend
- `src/RepoQL.Explore/ExploreOrchestrator.cs` — orchestrator to modify
- `src/RepoQL.Explore/OptionValue.cs` — intent modifier values
- `src/RepoQL.Explore/UtilityCalculator.cs` — utility formula (unchanged)

## Error Policy

Allocation failures fall back gracefully:
1. If `AllocateWithClusters` throws, fall back to calling existing `Allocate` with all results as a flat list
2. If cluster EV computation fails for a cluster, use `max(member.Confidence)` without modifiers
3. If budget invariant is violated (rendered > budgeted), truncate output at render time with `...` indicator
