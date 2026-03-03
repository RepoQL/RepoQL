# Plan: Cross-Result Clustering

Implements: Explore rendering improvements — group spatially related results

## Scope

**Covers:**
- Detecting spatial clusters (results in the same directory or namespace)
- Sorting co-located results adjacent to each other in output
- Cluster header showing shared context

**Does not cover:**
- Graph-based relationship clustering (CALLS, REFERS_TO edges) — future work
- Merging clusters into single results (each result remains individually addressable)
- Changes to scoring or ranking within clusters

## Enables

Once Cross-Result Clustering exists:
- **Agents see cohesive areas** — `AuthService`, `TokenValidator`, `JwtHandler` in `src/Auth/` presented as a group, not scattered across results
- **Faster navigation** — agent reads one cluster, decides to explore that area or skip it entirely
- **Natural read() follow-up** — cluster header suggests `file:///src/Auth/**` as a natural scope for deeper reading

## Prerequisites

- Results carry full URIs with file paths (already do)
- `OutputComposer` controls result ordering and spacing (already does)

## North Star

When 4 results are in the same directory, the agent instantly sees "these form a cohesive area" and can reason about the area as a unit — not as 4 independent findings.

## Done Criteria

### Cluster Detection

- The system shall group results sharing the same parent directory into clusters
- A cluster shall require at least 2 results to form (singletons remain ungrouped)
- When results span multiple directory levels, clustering shall use the most specific shared ancestor
- Clustering shall run after scoring and before rendering

### Cluster Ordering

- Results within a cluster shall maintain their original score-based ordering
- Clusters shall be ordered by the maximum confidence of their members
- Ungrouped results shall interleave with clusters based on their confidence relative to cluster maximums

### Cluster Display

- Each cluster with 3 or more members shall have a header line showing the shared path
- The header format shall be: `── src/Auth/ (4 results) ──`
- Clusters with exactly 2 members shall sort adjacently but without a header (too noisy)
- The cluster header shall cost no more than 15 tokens

### Budget Accounting

- Cluster headers shall be deducted from the total token budget before allocation
- With 5 clusters of 3+, overhead is ~75 tokens — less than 5% of a typical 1500-token budget

## Constraints

- **Post-scoring only** — clustering is a display concern, not a ranking signal
- **Score ordering within cluster** — highest-scored result in the cluster appears first
- **No cross-scheme clustering** — `file:///` and `help:///` results don't cluster together even if paths overlap
- **Opt-out at low counts** — with fewer than 6 total results, clustering adds overhead without value; skip it

## References

- `src/RepoQL.Explore/OutputComposer.cs:18` — `Compose` method, result iteration
- `src/RepoQL.Explore/ExploreResult.cs` — `Uri` field for path extraction
- `src/repoql.explore/search/filegrouper.cs` — existing within-file grouping (extend concept one level up)

## Error Policy

If URI parsing fails for a result (malformed URI), leave it ungrouped. Clustering is best-effort — a parse error in one result never affects others.
