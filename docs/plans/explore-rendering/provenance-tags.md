# Plan: Provenance Tags

Implements: Explore rendering improvements — surface dominant scoring signal per result

## Scope

**Covers:**
- Tracking which scoring signal dominated each result's ranking
- Surfacing provenance in Standard and Rich representations
- Computing provenance from existing score components

**Does not cover:**
- Changes to scoring weights or algorithms
- Exposing raw score values (only the dominant *signal type*)
- Provenance in Minimal or Compact representations (too dense)

## Enables

Once Provenance Tags exist:
- **Query refinement is guided** — "high name match, low semantic" means the name matched but content may diverge; try different keywords
- **Agent learns the codebase** — "semantic match" on an unexpected file reveals conceptual relationships the agent didn't know about
- **Debug search quality** — when results seem wrong, provenance shows *why* they ranked high

## Prerequisites

- Score components available on `ObjectCandidate`: `RegexHitScore`, `ChunkOverlapScore`, `NameHitScore`, `SemanticScore` (already exist)
- `SearchResult` flows through to `ExploreResult` (already does)

## North Star

An agent seeing a result immediately understands *why* it's there — not just *that* it's there. One word, zero extra cognitive load.

## Done Criteria

### Provenance Computation

- When the dominant scoring signal for a result is semantic similarity, the provenance shall be `semantic`
- When the dominant signal is symbol/function name match, the provenance shall be `name`
- When the dominant signal is regex/keyword hit, the provenance shall be `lexical`
- When the dominant signal is chunk overlap (BM25), the provenance shall be `content`
- When no single signal dominates (top two within 20% of each other), the provenance shall be `mixed`
- The provenance shall be computed from the score components on the winning search path (cheap or JIT)

### Data Flow

- `SearchResult` shall carry a `Provenance` field (string, nullable)
- `ExploreResult` shall carry the provenance through from `SearchResult`
- The provenance shall be set during scoring in both standard and JIT paths
- For document-level results without object scores, provenance shall be derived from document search scores

### Rendering

- In Standard representation, provenance shall appear after the headline: `file:///src/Auth.cs  Token validation service (semantic)`
- In Rich representation, provenance shall appear in the same position as Standard
- In Compact and Minimal, provenance shall be omitted
- Provenance shall be rendered in parentheses with no additional formatting

### Budget Impact

- Provenance adds at most 12 tokens per result (` (semantic)`)
- The token estimator shall account for provenance in Standard and Rich estimates

## Constraints

- **Read-only** — provenance is computed from existing scores, never influences scoring
- **One word** — provenance is a single label, not a score breakdown
- **Nullable** — if scores aren't available (standard path with no object search), omit provenance rather than guess

## References

- `src/repoql.explore/search/objectsearchtypes.cs:82-99` — `ObjectCandidate` score fields
- `src/RepoQL.Explore/ExploreResult.cs` — result record to extend
- `src/RepoQL.Explore/RepresentationFormatter.cs:51` — `FormatStandard`
- `src/repoql.explore/search/objectsearchtypes.cs:182-188` — `GetFinalWeights` (scoring weights by intent)

## Error Policy

If all score components are zero (shouldn't happen but might in edge cases), set provenance to null. Never display `(unknown)` — silence is better than noise.
