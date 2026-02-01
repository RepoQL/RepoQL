# Plan: Pattern Matching

Implements: [Line-Range Globbing Design](../../designs/future/globbing.md) — Pattern Matching and URI Simplification

## Scope

**Covers:**
- `IUriSimplifier` interface and implementation
- Updated `UriRegistryExtensions.MatchPattern` using line-range approach
- Entity expansion (symbols → line ranges)
- Integration of `ILineRangeCalculator` for set operations
- Simplification of results to canonical URIs
- Tests for the full pattern matching flow

**Does not cover:**
- Registry model (Plan: Registry Model — prerequisite)
- Indexing integration (Plan: Indexing Integration — prerequisite)
- Line range calculator (Plan: Line Range Calculator — prerequisite)
- SQL surface (Plan: SQL Surface)

## Enables

Once Pattern Matching exists:
- **North star declarations become true** — exclude line ranges, get partial symbols
- **Plan: SQL Surface** can expose the functionality via UDF
- **Read tool** can use patterns like `src/**/*.cs#symbol=*;!#line=1,30`

This is the core algorithm that fulfills the globbing north star.

## Prerequisites

- Plan: Registry Model complete — `SymbolEntry`, `FileEntry`, `LineRange`
- Plan: Indexing Integration complete — registry has span data
- Plan: Line Range Calculator complete — union/subtract operations

## North Star

Any pattern expressible in the syntax produces correct results. Exclusions carve out regions precisely. Results are canonical URIs (symbol where exact, line range where partial).

## Done Criteria

### IUriSimplifier

- The interface shall define `Simplify(fileUri, range, entry)` returning `RepoUri`
- The implementation shall return file URI when range equals full file
  - `(1, 200)` with `LineCount = 200` → `file:///path`
- The implementation shall return symbol URI when range exactly matches a symbol
  - `(85, 95)` matching `IAuthService(85, 95)` → `file:///path#symbol=IAuthService`
- The implementation shall return line range URI otherwise
  - `(31, 80)` with no exact match → `file:///path#line=31,80`

### Entity Expansion

- When pattern has no fragment, MatchPattern shall expand files to full line ranges
- When pattern has `#symbol=*`, MatchPattern shall expand to all symbol spans
- When pattern has `#symbol=Foo*`, MatchPattern shall expand to matching symbol spans
- When pattern has `#line=N,M`, MatchPattern shall use explicit range
- When symbol has zero span (0, 0), MatchPattern shall skip that symbol

### Pattern Matching Flow

- MatchPattern shall parse pattern into positives and negatives
- MatchPattern shall collect candidate files matching container patterns
- MatchPattern shall expand entities to line ranges per file
- MatchPattern shall union positive ranges using LineRangeCalculator
- MatchPattern shall subtract negative ranges using LineRangeCalculator
- MatchPattern shall simplify remaining ranges to canonical URIs
- MatchPattern shall return `IEnumerable<RepoUri>` of results

### Fragment Pattern Handling

- When negative pattern is `!#line=N,M`, subtract that range from all files
- When negative pattern is `!#symbol=Foo`, subtract that symbol's span
- When negative pattern has container (e.g., `!src/test/**`), only subtract from matching files

### Integration Tests

- Test: `src/**/*.cs#symbol=*` returns all symbols
- Test: `src/**/*.cs#symbol=*;!#line=1,30` returns symbols minus header region
- Test: `src/Foo.cs#symbol=MyClass;!#line=35,40` returns partial ranges when exclusion splits symbol
- Test: Exclusion that doesn't overlap returns unchanged
- Test: Exclusion that fully covers returns nothing
- Test: Pattern with no matches returns empty

## Constraints

- **Registry-based** — design specifies no database queries during pattern match
- **Snapshot semantics** — iterate registry once at start; no mid-operation updates
- **Line granularity** — character-level precision not supported (design decision)

## References

- [Line-Range Globbing Design](../../designs/future/globbing.md) — full design
- [Pattern Matching Flow](../../flows/future/globbing/pattern-matching.md) — detailed flow
- [UriRegistryExtensions.cs](../../../src/RepoQL.Contracts/UriRegistry/UriRegistryExtensions.cs) — current MatchPattern
- [UriPatternMatcher.cs](../../../src/RepoQL.Contracts/UriPatternMatcher.cs) — pattern parsing

## Error Policy

- Invalid pattern syntax: throw `ArgumentException` with descriptive message
- Symbol with no span: skip symbol, continue (already handled by zero-span check)
- File with no LineCount: cannot simplify to file URI; will return line range URI instead

Pattern matching should not fail on valid patterns — malformed registry data results in degraded results, not errors.
