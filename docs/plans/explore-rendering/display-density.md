# Plan: Display Density

Implements: Explore rendering improvements — maximize information value per token

## Scope

**Covers:**
- Intent-aware headline trimming (reduce metadata in Inventory)
- Shorter child fragment URIs (strip redundant namespace from `#symbol=`)
- Minimal representation always includes URI (prevent invisible duplicates)
- Confidence tier separator (visual cliff between strong and weak results)

**Does not cover:**
- Changes to x-ray headline generation (headlines produced at index time stay the same)
- Changes to representation level selection (which level is chosen stays the same)
- New representation levels

## Enables

Once Display Density improvements exist:
- **Inventory budgets go further** — shorter headlines fit more results in the same token budget
- **Children are cheaper** — `#symbol=Foo` instead of `#line=50,71&symbol=Namespace.Class.Foo` saves ~10 tokens per child
- **No invisible duplicates** — Minimal with URI means worktree copies (or any duplicates) are visually distinct
- **Result quality is scannable** — a blank line between 85% and 12% results makes the cliff obvious

## Prerequisites

- X-ray headlines use pipe-delimited format: `Description | type | size | tokens | sections` (already established)
- `RepresentationFormatter` handles all representation levels (already does)
- `OutputComposer` controls spacing between results (already does)

## North Star

Every token in explore output advances the agent toward their goal. No token spent on metadata the agent can't act on at this intent level. No duplicate that wastes budget without being visible.

## Done Criteria

### Intent-Aware Headlines

- For Inventory intent, headlines shall show: `Description | ~Nk tok`
  - Type, size in bytes, line count, and section list shall be omitted
  - Token cost is kept because it answers "how expensive is this to read?"
- For Locate intent, headlines shall show: `Description | type | ~Nk tok | first 3 sections`
  - Size in bytes and line count omitted; section list truncated to 3
- For Inspect intent, headlines shall use the full pipe-delimited format (unchanged)
- The trimming shall happen in `RepresentationFormatter`, not in x-ray generation

### Shorter Child Fragments

- When a child's URI shares the same base as its parent, the fragment shall show only the symbol's simple name
  - `#symbol=RepoQL.Explore.Search.ConfidenceNormalizer.NormalizeResult` becomes `#symbol=NormalizeResult`
- When the child fragment includes both `line=` and `symbol=`, only `#symbol=SimpleName` shall be shown
  - The line range is recoverable from the symbol; displaying both is redundant
- The full URI shall remain available in the `ExploreResult` for programmatic access; only display is shortened

### Minimal Includes URI

- Minimal representation shall always include the URI
- The format shall be: `uri  headline` (two spaces between URI and headline, matching Compact)
- When this causes Minimal to exceed its token estimate, the estimator shall be updated accordingly
- This eliminates the case where duplicate files produce identical output lines

### Confidence Tier Separator

- When the confidence drop between consecutive results exceeds 30 percentage points, a separator shall be inserted
- The separator format shall be a blank line (zero additional tokens — just `\n\n` instead of `\n`)
- At most one separator shall appear per output (the first major cliff)
- When no cliff exceeds 30 points, no separator is inserted
- The separator shall only apply when confidence scores are shown (i.e., when keywords are provided)

## Constraints

- **Headline content unchanged** — trimming removes metadata fields, never alters the description text
- **Full URI in data model** — display shortening is rendering-only; `ExploreResult.Uri` retains the full fragment
- **Backward compatible** — agents using explore output for `read()` follow-up get the same URIs they always did
- **Inspect untouched** — Inspect representation is already depth-optimized; no changes

## References

- `src/RepoQL.Explore/RepresentationFormatter.cs:414` — `ShortHeadline` (existing pipe-splitting logic)
- `src/RepoQL.Explore/RepresentationFormatter.cs:360` — `AppendHeader` (URI/headline composition)
- `src/RepoQL.Explore/RepresentationFormatter.cs:386` — `GetDisplayUri` (child fragment shortening)
- `src/RepoQL.Explore/OutputComposer.cs:31` — result iteration loop (separator insertion point)
- `src/RepoQL.Explore/ExploreTokenEstimator.cs` — Minimal estimate needs updating

## Error Policy

If headline parsing fails (no pipe delimiter found), use the full headline unchanged. Trimming is best-effort — malformed headlines display as-is.
