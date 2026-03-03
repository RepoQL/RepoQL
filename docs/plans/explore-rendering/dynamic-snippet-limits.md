# Plan: Dynamic Snippet Limits

Implements: Explore rendering improvements — MaxSnippetsPerFile scales with budget and intent

## Scope

**Covers:**
- Replacing `MaxSnippetsPerFile = 3` constant with a dynamic calculation
- Scaling snippet count based on per-file budget allocation and intent
- Applying in both standard and JIT search paths

**Does not cover:**
- Changes to snippet content or extraction (what's in a snippet stays the same)
- Changes to how snippets are rendered (RepresentationFormatter unchanged)
- Snippet quality ranking (existing score-based ordering preserved)

## Enables

Once Dynamic Snippet Limits exists:
- **High-budget Inspect on single file shows more code** — 5000 tokens on 1 file with 10 relevant methods shows all 10, not just 3
- **Budget spent on content, not wasted** — tokens that would go unused now show additional relevant snippets
- **Better Inspect experience** — the intent that means "I know the target, show me depth" actually delivers depth

## Prerequisites

- Per-file budget available in `AllocateWithinFile` (already calculated by Level 1 allocation)
- Snippet token estimates available via `ExploreTokenEstimator` (already exist)

## North Star

When an agent asks for depth on a file, every relevant symbol in that file gets shown, limited only by budget — not by an arbitrary cap.

## Done Criteria

### Dynamic Calculation

- The snippet limit shall be calculated from per-file budget divided by estimated snippet cost
- The minimum snippet limit shall be 2 (always show at least the top 2)
- The maximum snippet limit shall be 15 (prevent unbounded expansion)
- The calculation shall use `ExploreTokenEstimator.EstimateRich` for snippet cost estimation

### Intent Scaling

- For Inventory intent, the snippet limit shall be capped at 3 (breadth over depth)
- For Locate intent, the snippet limit shall be capped at 5
- For Inspect intent, the snippet limit shall use the full dynamic calculation (up to 15)
- For Explain intent, the snippet limit shall be capped at 8

### Integration

- `FileGrouper.MaxSnippetsPerFile` shall become a method parameter, not a constant
- Both `FileGrouper.Group` and JIT result conversion in `ExploreSearchEngine` shall accept the dynamic limit
- The constant `3` shall be removed; no code path shall use a hardcoded snippet limit

## Constraints

- **Budget is still the real limit** — dynamic snippets don't increase total budget, they redistribute within it
- **Score ordering preserved** — top-scored snippets first, additional snippets are lower-ranked
- **Headline fallback unchanged** — snippets beyond the limit still appear as headline-only children

## References

- `src/repoql.explore/search/filegrouper.cs:25` — `MaxSnippetsPerFile = 3`
- `src/repoql.explore/search/filegrouper.cs:54` — `.Take(MaxSnippetsPerFile)`
- `src/repoql.explore/search/iexploresearchengine.cs` — JIT path uses `FileGrouper.MaxSnippetsPerFile`
- `src/RepoQL.Explore/ExploreTokenEstimator.cs` — `EstimateRich`

## Error Policy

If snippet cost estimation returns zero or negative, fall back to the minimum limit (2). Log a warning — zero-cost estimates indicate a bug in the estimator.
