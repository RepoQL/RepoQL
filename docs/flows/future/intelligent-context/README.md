# Intelligent Context Selection Flows

Transforms explore from "here are matching files" to "here's the context you need, organized for understanding."

## Overview

```
Phase 1          Phase 2           Phase 3          Phase 4           Phase 5
─────────        ─────────         ─────────        ─────────         ─────────
focused-         query-            simhash-         clustered-        budget-
snippets         expansion         dedup            output            allocation

Use chunk        Expand abbrevs    Fingerprint      Group results     Three-level
scores to        + casing before   files at index   by directory,     cluster →
show relevant    search. Fuse      time. Detect     type, relation.   file →
regions, not     original +        near-dupes at    Label clusters.   object budget
whole files.     expanded via      query time.      Show structure.   distribution.
                 RRF.              Headline dupes.
```

Each phase is independently valuable. Later phases build on earlier ones but don't require them.

## Current State

Today's explore pipeline: search → flat ranking → two-level budget allocation (file → children) → representation selection (Minimal/Compact/Standard/Rich) → output.

| What exists | What's missing |
|-------------|----------------|
| Chunk scores from semantic search | Not used for snippet selection |
| BM25 + semantic + fuzzy scoring | No query expansion for abbreviations |
| Flat ranked results | No duplicate detection |
| Two-level budget allocation | No clustering or grouping |
| 4 representation levels | No focused snippet level |

## Phase Index

| Phase | Flow | Touches | Effort | Impact |
|-------|------|---------|--------|--------|
| 1 | [focused-snippets](focused-snippets.md) | Allocation, rendering | Low | High |
| 2 | [query-expansion](query-expansion.md) | Search entry, scoring | Low | Medium |
| 3 | [simhash-dedup](simhash-dedup.md) | Indexing, search, rendering | Low | Medium |
| 4 | [clustered-output](clustered-output.md) | Post-search, rendering | Medium | High |
| 5 | [budget-allocation](budget-allocation.md) | Allocation (rewrite) | Medium | High |

## Reading Order

1. `focused-snippets` — smallest change, biggest per-file improvement
2. `query-expansion` — independent of the rest, improves all downstream
3. `simhash-dedup` — requires indexing change but simple; enables phase 4
4. `clustered-output` — requires phase 3 for duplicate clusters; reshapes output
5. `budget-allocation` — requires phase 4 for cluster input; ties everything together

## Key Invariants

| Invariant | Flows Involved |
|-----------|----------------|
| Budget is a contract — spend exactly what was asked | All phases |
| Never return incomplete results as complete | All phases |
| Duplicates are demoted, not hidden — awareness without waste | simhash-dedup, budget-allocation |
| Cluster labels come from facts, not inference | clustered-output |
| Each phase degrades gracefully if data is missing | All phases |

## Related

- [ideas/](../../../ideas/) — Algorithm research and synergy analysis
- [current-state/xray.md](../../../current-state/xray.md) — Current explore tool documentation
- [current-state/search.md](../../../current-state/search.md) — Current search infrastructure
