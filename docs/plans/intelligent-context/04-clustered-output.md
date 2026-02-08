---
description: Plan for grouping explore results by directory, content type, and duplicate relationship
tags: [explore, clustering, output, rendering]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Clustered Output

Implements: [Intelligent Context Design](../../designs/future/intelligent-context.md) — Components (ResultClusterer)

## Scope

**Covers:**
- `IResultClusterer` interface and `PathBasedClusterer` implementation
- `ResultCluster` record and `ClusterType` enum
- Cluster assignment strategies: duplicate groups, directory prefix, content type, ungrouped
- Cluster labeling from facts (paths, types, relationships)
- Cluster ordering by aggregate score
- `OutputComposer` cluster header rendering
- Cluster overhead budget accounting

**Does not cover:**
- Duplicate detection (Plan: 03-simhash-dedup) — this plan consumes DuplicateOf annotations if present
- Focused snippets (Plan: 01-focused-snippets) — renders within clusters, no interaction
- Cluster-level budget allocation (Plan: 05-three-level-allocation) — this plan only groups and renders; allocation changes live there
- Spectral clustering or graph-based module detection (deferred per design)

## Enables

Once Clustered Output exists:
- **Agents see structure** — "3 files in src/Auth/, 2 docs, 1 config" instead of a flat list
- **Plan 05** can allocate budget at cluster level — clusters are the input for three-level allocation
- **Duplicate groups visible** — when Plan 03 exists, duplicate clusters show canonical + copies together

## Prerequisites

- `ExploreResult` with `DuplicateOf` and `HammingDistance` fields (from Plan 03, or null if Plan 03 not deployed)
- `ExploreResult.SemanticType` populated by search engine (already exists)
- `OutputComposer` renders `RenderingDecision[]` (already exists)

## North Star

Results reveal where in the codebase the answer lives, not just which files matched. The agent sees "these 3 auth files cluster together, here's a related doc, and here's a config file" without reconstructing structure from paths.

## Done Criteria

### IResultClusterer

- The `IResultClusterer` interface shall define `IReadOnlyList<ResultCluster> Cluster(IReadOnlyList<ExploreResult> results)`
- `ResultCluster` shall include: `Label` (string), `Type` (ClusterType), `AggregateScore` (double), `Members` (IReadOnlyList<ExploreResult>)
- `ClusterType` shall include: `Directory`, `Duplicate`, `ContentType`, `Ungrouped`

### PathBasedClusterer — Duplicate Strategy

- When any result has `DuplicateOf` set, the clusterer shall form a Duplicate cluster containing the canonical result and all its duplicates
- The cluster label shall be `"{canonical_filename} ({count} copies)"` (e.g., `"AuthService.cs (3 copies)"`)
- When no results have `DuplicateOf` set (Plan 03 not deployed), this strategy produces no clusters

### PathBasedClusterer — Directory Strategy

- Results sharing a directory path prefix shall form a Directory cluster
  - Minimum cluster size: 2 results
  - Single results in a directory do not form a cluster
- The prefix shall be the shortest path that groups >= 2 results
  - Do not cluster at root level (e.g., `src/` alone is too broad unless it contains only 2-3 results total)
- When a result is already in a Duplicate cluster, it shall not be re-assigned to a Directory cluster
- The cluster label shall be `"{path_prefix} ({count} files)"` (e.g., `"src/Auth/ (4 files)"`)

### PathBasedClusterer — Content Type Strategy

- After duplicate and directory strategies, remaining results with distinct non-code semantic types shall form ContentType clusters
  - `markdown.*` → label "Documentation"
  - `data.yaml`, `data.json`, `data.toml` → label "Configuration"
  - `schema.*` → label "Schemas"
  - Code files (`code.*`) do not form type clusters — they cluster by directory instead
- Minimum cluster size: 2 results

### PathBasedClusterer — Ungrouped

- Results not assigned to any cluster shall be treated as individual `Ungrouped` entries
- Each ungrouped result becomes a single-member cluster with `Type = Ungrouped`
- Ungrouped clusters have no header rendered

### Cluster Ordering

- Clusters shall be ordered by `AggregateScore` descending
- `AggregateScore` = max(member.Confidence) across cluster members
- Within each cluster, members ordered by Confidence descending

### Cluster Labeling

- Labels shall be 40 characters or fewer
  - When path exceeds 40 chars, truncate middle with `...`
- Labels shall come from facts only — paths, types, counts, relationships
- Labels shall never infer semantic meaning (e.g., never label a cluster "Authentication Module" from path `src/Auth/`)

### Output Rendering

- Each non-Ungrouped cluster shall render a header line: `── {label} ──` with fill characters
- Cluster headers shall be visually distinct from result lines
- Results within a cluster render below the header, using their allocated representation level
- Ungrouped results render after all clustered results with no header
  - When there are >= 3 ungrouped results, render under `── Other ──` header
- When only 1 cluster exists (or all results are ungrouped), no cluster headers shall be rendered

### Budget Overhead

- Cluster headers cost ~10 tokens each
- The OutputComposer shall deduct cluster header costs from the total budget before passing to allocation
  - `distributable = totalBudget - (nonUngroupedClusterCount * 10) - footerCost`

### Minimum Budget Threshold

- When `tokenBudget < 500`, clustering shall be skipped entirely — all results treated as Ungrouped
  - At 500 tokens, 4 cluster headers = 40 tokens = 8% overhead, which is excessive
  - Below the threshold, the clusterer returns all results as single-member Ungrouped clusters

### Passthrough

- When 0 or 1 results, the clusterer shall return a single-member Ungrouped cluster — no headers rendered
- When no clusters form (every result ungrouped), output shall be identical to today's flat list

## Constraints

- **Labels from facts** — never infer; paths, types, and duplicate relationships are facts
- **No allocation changes** — this plan renders clusters; budget allocation within clusters is Plan 05
- **Code files cluster by directory** — content type clustering is for non-code formats only
- **Strategy order matters** — duplicate groups first, then directory prefix, then content type, then ungrouped; a result joins only one cluster

## References

- [Intelligent Context Design](../../designs/future/intelligent-context.md) — IResultClusterer contract, ClusterType enum
- [Clustered Output Flow](../../flows/future/intelligent-context/clustered-output.md) — full stage-by-stage flow
- `src/RepoQL.Explore/OutputComposer.cs` — rendering to extend
- `src/RepoQL.Explore/ExploreOrchestrator.cs` — insert clustering step
- `src/RepoQL.Explore/ExploreResult.cs` — carries DuplicateOf and SemanticType

## Error Policy

Clustering failures must not block explore output:
1. If clustering throws, log warning and return all results as Ungrouped single-member clusters — output renders as flat list
2. If label generation fails for a cluster, use fallback label "Results ({count} files)"
3. Invalid or empty cluster members are filtered out silently
