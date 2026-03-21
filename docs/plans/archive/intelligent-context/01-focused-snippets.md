---
description: Plan for using semantic search chunk scores to render relevant file regions instead of falling back to signatures
tags: [explore, snippets, chunks, representation, allocation]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Focused Snippets

Implements: [Intelligent Context Design](../../designs/future/intelligent-context.md) — Focused Representation section

## Scope

**Covers:**
- Propagating best chunk location from semantic search through to ExploreResult
- `Representation.Focused` enum value
- `ExploreTokenEstimator.EstimateFocused` method
- `OptionValue` extension for Focused value weights
- `RepresentationFormatter.FormatFocused` method
- `ValueBasedDecisionEngine` PickBestFit logic to consider Focused
- Lazy snippet population for Focused results

**Does not cover:**
- Query expansion (Plan: 02-query-expansion)
- Duplicate detection (Plan: 03-simhash-dedup)
- Clustering or cluster-level allocation (Plans: 04, 05)
- Changes to the search scoring pipeline

## Enables

Once Focused Snippets exist:
- **Agents see actual code** when a file is too large for Rich, instead of just signatures
- **Token efficiency improves** — focused region costs 80-250 tokens vs 200-500 for full file
- **Follow-up read() calls decrease** — the relevant region is already in context
- **Plan 05** can include Focused in the representation progression for three-level allocation

## Prerequisites

- Chunk scores already computed by `_search_semantic` and available in `JitObjectSearchService.GetChunkScores`
- `snippet()` SQL macro operational
- `ValueBasedDecisionEngine` and `RepresentationFormatter` as described in design contracts

## North Star

When a file is too large for Rich but semantic search found a matching region, the agent sees that region's code — not just method signatures. The representation costs less than Rich but delivers actionable content.

## Done Criteria

### Chunk Propagation

- The ExploreSearchEngine shall attach `BestChunkStartLine` and `BestChunkEndLine` to each SearchResult that has chunk scores
  - When multiple chunks score within 5% of each other, prefer the chunk closest to file start
  - When no chunk scores exist (no semantic search), both fields shall be null
- The SearchResult-to-ExploreResult conversion shall carry `BestChunkStartLine` and `BestChunkEndLine` through unchanged
- Both search paths shall propagate chunk locations:
  - **Standard path** (Inventory): Extract best chunk from `ChunkProximityBooster` scores after boosting
  - **JIT path** (Locate, Inspect, Explain): Extract best chunk from `DocumentExpansionCandidate.HighScoringChunks` in `ConvertJitResults()`
  - The JIT path currently discards `HighScoringChunks` during conversion — this must be fixed

### Representation Enums

The codebase has two representation enums that must both gain `Focused`:

- **`Representation`** (`Representation.cs`) — used by `ExploreTokenEstimator` for rendering cost estimation
- **`RepresentationLevel`** (`OptionValue.cs`) — used by `ValueBasedDecisionEngine` for allocation planning and `OptionValue.GetValue()` for the utility value matrix

Both enums shall include `Focused` between `Standard` and `Rich`:
- `OptionValue.GetNextLevel(Standard)` shall return `Focused`
- `OptionValue.GetNextLevel(Focused)` shall return `Rich`
- `OptionValue.GetLevelProgression()` shall return `[Minimal, Compact, Standard, Focused, Rich]`

### Value Matrix

- `OptionValue.GetValue` shall return Focused values per intent:
  - Inventory: 0.15
  - Locate: 0.6
  - Inspect: 0.85
  - Explain: 0.7

### Token Estimation

- `ExploreTokenEstimator.EstimateFocused` shall return `int.MaxValue` when `BestChunkStartLine` is null
- When chunk location is available, the estimate shall include headline tokens + URI tokens + line indicator (5) + chunk line count with 6 context lines at ~10 tokens/line + code fence overhead (4)
- `ExploreTokenEstimator.Estimate(result, Representation.Focused)` shall delegate to `EstimateFocused`

### Allocation Integration

- The PickBestFit logic in `ValueBasedDecisionEngine` shall consider Focused after Rich fails and before Standard
  - When `EstimateFocused(result) <= allocation` and `BestChunkStartLine` is not null, select Focused
  - When `BestChunkStartLine` is null, skip Focused entirely

### Snippet Pre-Fetch

- After allocation produces `ClusterDecision[]` (or `RenderingDecision[]` when clusters unavailable), the orchestrator shall scan for Focused allocations and batch-fetch snippets asynchronously before rendering
- Each Focused result's snippet shall be fetched via `snippet()` macro using `file:///path#line={start},{end}` with context of 3 lines
- If a snippet fetch returns empty or throws, the result shall be downgraded to Standard before rendering
- The pre-fetch step resolves the mismatch between async `snippet()` calls and the synchronous `RepresentationFormatter`

### Rendering

- `RepresentationFormatter.FormatFocused` shall render: confidence + URI + headline on first line, `lines {start}-{end}:` indicator, code fence with pre-fetched snippet content
- When chunk region is < 3 lines, context padding shall expand to 5 lines

### Passthrough

- When all chunk locations are null (no semantic search), the pipeline shall produce output identical to the existing pipeline
  - Focused is never selected because `EstimateFocused` returns `int.MaxValue`

## Constraints

- **No search changes** — this plan only modifies the post-search rendering path; search scoring is unchanged
- **Lazy population** — snippets fetched only for results allocated to Focused, not eagerly for all results
- **Focused for files only** — symbol/object results already have spans and render as Rich; Focused applies to document-level results

## References

- [Intelligent Context Design](../../designs/future/intelligent-context.md) — Focused Representation section, value matrix, token estimation formula
- [Focused Snippets Flow](../../flows/future/intelligent-context/focused-snippets.md) — full stage-by-stage flow
- `src/RepoQL.Explore/ExploreResult.cs` — record to extend
- `src/RepoQL.Explore/Representation.cs` — enum to extend
- `src/RepoQL.Explore/OptionValue.cs` — value matrix to extend
- `src/RepoQL.Explore/ExploreTokenEstimator.cs` — estimator to extend
- `src/RepoQL.Explore/RepresentationFormatter.cs` — formatter to extend
- `src/RepoQL.Explore/ValueBasedDecisionEngine.cs` — allocation to modify

## Error Policy

Focused snippet failures are recoverable:
1. If `snippet()` returns empty or throws, fall back to Standard representation
2. If chunk line numbers are out of range, clamp to file boundaries before fetching
3. Log warning on fallback; never fail the explore operation
