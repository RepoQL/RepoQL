# Plan: Search Quality Signal

Implements: Explore rendering improvements — footer trust communication

## Scope

**Covers:**
- Absolute quality tier in explore footer (`strong | moderate | weak | exhaustive`)
- Search coverage stats (`N of M scored above threshold`)
- Integration with existing `FormatStatusFooter`

**Does not cover:**
- Changes to scoring algorithms (search quality is reported, not changed)
- Changes to confidence normalization (relative scores remain)
- Read tool footer changes

## Enables

Once Search Quality Signal exists:
- **Agents trust results** — "strong match" means stop looking; "weak match" means refine the query
- **No verification tax** — agents don't need to run subagents to confirm completeness
- **Better query refinement** — coverage stats tell agents whether to widen scope or narrow keywords

## Prerequisites

- Raw scores available in `SearchEngineResult` (already exist via `SearchResult.RawScore`)
- Document/object counts available from search engine (already returned as `TotalDocumentsMatched`, `TotalObjectsMatched`)

## North Star

An agent seeing explore output can immediately answer: "Should I trust this and move on, or keep looking?" One glance at the footer, no second-guessing.

## Done Criteria

### Quality Tier

- The footer shall include a quality tier derived from the top raw score
- When the top result's raw score exceeds a strong-match threshold, the tier shall be `strong`
- When the top result's raw score is moderate, the tier shall be `moderate`
- When the top result's raw score is low but results exist, the tier shall be `weak`
- When no keywords were provided (pure Inventory), the tier shall be `exhaustive`
- The quality tier thresholds shall be calibrated against existing `ConfidenceScoringTests` queries

### Coverage Stats

- The footer shall include result coverage when keywords are provided
- The format shall be `N of M above threshold` where N is displayed results and M is total documents searched
- When the scope is narrow (under 20 documents), coverage shall be omitted (not informative)
- When all documents scored above threshold, coverage shall show `N matches (all in scope)`

### Footer Integration

- The quality tier and coverage shall appear in the existing bracket-delimited footer
- The format shall be: `[quality: strong | N of M above threshold | 1.2k tok | 150 ms | index: ready | semantic: ready]`
- Quality and coverage shall appear first (most actionable information front-loaded)

## Constraints

- **No new API parameters** — this is output-only; agents don't request quality tiers
- **Thresholds are internal** — the mapping from raw score to tier is an implementation detail, not a promise
- **Budget-neutral** — quality signal adds ~10-15 tokens to footer, not more

## References

- `src/RepoQL.Explore/RepresentationFormatter.cs:226` — `FormatStatusFooter`
- `src/repoql.explore/search/confidencenormalizer.cs` — existing score-to-confidence mapping
- `src/repoql.explore/search/iexploresearchengine.cs` — `SearchEngineResult` with counts

## Error Policy

If raw scores are unavailable (edge case), omit quality tier from footer. Never show a quality tier you can't back up.
