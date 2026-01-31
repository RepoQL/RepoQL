---
description: Plan for web UI Search view - explore testing with score breakdown
tags: [ui, plan, search, explore, scoring]
audience: { human: 40, agent: 60 }
purpose: { plan: 100 }
---

# Plan: Web UI Search View

Implements: [Web UI Design](../designs/web-ui.md) — Search View, ISearchService

## Scope

**Covers:**
- `ISearchService` interface and implementation
- Search view with all explore parameters exposed
- Readiness check before search
- Results display with score breakdown (semantic, BM25, fuzzy)
- Boost/penalize effect indicators
- Link to Inspect view from results

**Does not cover:**
- A/B comparison of different parameters (stretch goal)
- "Why not found?" diagnostic for specific files (stretch goal)
- Symbol search toggle (uses same view, different intent)

## Enables

Once Search view exists:
- **Explore testing** — Developers can test all explore parameters
- **Score visibility** — Key north star: see WHY results ranked
- **Search debugging** — Understand boost/penalize effects
- **Readiness verification** — Know if results are trustworthy before searching

## Prerequisites

- Plan: web-ui-1-foundation complete
- Plan: web-ui-3-inspect complete (for "Inspect" links)
- gRPC `Explore` method and `ExecuteRawQuery` for score retrieval

## North Star

Test any explore configuration, see results with full score breakdown. Know immediately if index is ready. Understand why each result ranked where it did.

## Done Criteria

### ISearchService
- The SearchService shall accept `SearchParams` (keywords, intent, budget, scope, boost, penalize, limit)
- The SearchService shall check readiness before search via `scope_readiness()` query
- The SearchService shall execute explore via gRPC
- The SearchService shall retrieve score breakdown via `search()` macro query
- The SearchService shall return `SearchResult` with hits, readiness, timing
- Each `SearchHit` shall include: URI, headline, score, semantic score, BM25 score, fuzzy score, boost/penalize flags

### Search View
- The Search view shall be accessible via navigation (route: `/search`)
- The Search view shall display input fields for all parameters:
  - Keywords (text input, required for Locate/Inspect/Explain)
  - Intent (dropdown: Inventory, Locate, Inspect, Explain)
  - Token Budget (slider or input, 500-10000, default 2000)
  - Scope (text input, optional, placeholder shows glob example)
  - Boost patterns (text input, optional)
  - Penalize patterns (text input, optional)
  - Limit (number input, optional)
- The view shall display a Search button

### Parameter Validation
- When intent is Locate, Inspect, or Explain: keywords required
- When intent is Explain: show note "Requires OPENROUTER_API_KEY"
- Invalid scope pattern shall show validation error
- Budget shall clamp to 500-10000 range

### Readiness Display
- Before search executes, readiness shall be checked
- Readiness badge shall display:
  - ✓ Ready (green): all files embedded
  - ⚠ Partial (yellow): N files pending embedding
  - ✗ Not ready (red): embeddings not started
- Readiness shall show: total files, embedded count, pending count

### Search Execution
- When Search clicked, the view shall show loading state
- Readiness check runs first, then explore, then score retrieval
- When complete, results display

### Results Display
- Each result shall show:
  - File URI (clickable → Inspect)
  - Headline from X-ray summary
  - Combined score (prominent)
  - Score breakdown:
    - Semantic: X.XX
    - BM25: X.XX
    - Fuzzy: X.XX
  - If boosted: "Boosted by: {pattern}"
  - If penalized: "Penalized by: {pattern}"
- Results ordered by combined score descending
- Each result has "Inspect →" link

### Timing Display
- Search duration displayed after results (e.g., "Results in 127ms")

### Intent-Specific Behavior
- Inventory: Keywords optional, broad results
- Locate: Keywords required, standard results
- Inspect: Keywords required, detailed snippets in output
- Explain: Keywords required, shows LLM-synthesized answer

### Error Handling
- When no results: "No results found" with readiness status
- When API key missing for Explain: Show error with setup instructions
- When scope invalid: Show validation error, don't execute
- When connection lost: Show error with retry option

## Constraints

- **Score retrieval adds latency** — Run explore and search() in parallel where possible
- **No "Why not found?"** — Deferred; would require additional diagnostic queries
- **No A/B comparison** — Single search only; comparison is stretch goal

## References

- [Web UI Design](../designs/web-ui.md) — Search View section, ISearchService contract
- [Search Testing Flow](../flows/ui/search-testing.md) — Detailed specifications
- [Schema.md](../Schema.md) — `search()` macro parameters and return columns

## Error Policy

Search errors:
1. Display error message above results area
2. Show what succeeded (e.g., readiness check passed)
3. Clear error when new search executed

Partial failures:
1. If explore succeeds but score retrieval fails: Show results without breakdown, note "Score details unavailable"
2. If readiness check fails: Show warning, allow search to proceed

## Verification

| Scenario | How to verify |
|----------|---------------|
| Basic search | Search "authentication", verify results with scores appear |
| Readiness | Search in scope with pending embeddings, verify warning badge |
| Score breakdown | Verify semantic, BM25, fuzzy scores shown per result |
| Boost | Add boost pattern "Auth.*", verify affected results show "Boosted by" |
| Penalize | Add penalize "(?i)test", verify test files show "Penalized by" |
| Intent Locate | Select Locate, search, verify standard results |
| Intent Explain | Select Explain (with API key), verify prose answer |
| No results | Search nonsense term, verify "No results" with readiness |
| Inspect link | Click "Inspect →" on result, verify Inspect view loads |
| Timing | Verify duration shown (e.g., "127ms") |
