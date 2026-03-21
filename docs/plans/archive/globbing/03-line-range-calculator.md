# Plan: Line Range Calculator

Implements: [Line-Range Globbing Design](../../designs/future/globbing.md) — ILineRangeCalculator

## Scope

**Covers:**
- `ILineRangeCalculator` interface
- `LineRangeCalculator` implementation with Union and Subtract
- Unit tests for all edge cases

**Does not cover:**
- Registry model (Plan: Registry Model — prerequisite for LineRange)
- Pattern matching (Plan: Pattern Matching — consumer)
- URI simplification (Plan: Pattern Matching)

## Enables

Once Line Range Calculator exists:
- **Plan: Pattern Matching** can perform set operations on line ranges
- **Union** combines positive pattern matches
- **Subtract** removes excluded regions

This is pure algorithm — no external dependencies, highly testable.

## Prerequisites

- Plan: Registry Model complete — `LineRange` struct available

## North Star

Set operations on line ranges are correct, efficient, and handle all edge cases. Zero off-by-one errors.

## Done Criteria

### ILineRangeCalculator Interface

- The interface shall define `Union(IEnumerable<LineRange>)` returning `IReadOnlyList<LineRange>`
- The interface shall define `Subtract(included, excluded)` returning `IReadOnlyList<LineRange>`

### Union

- The Union shall merge overlapping ranges into single range
  - `[(10, 30), (25, 45)]` → `[(10, 45)]`
- The Union shall merge adjacent ranges (end + 1 = start)
  - `[(10, 20), (21, 30)]` → `[(10, 30)]`
- The Union shall preserve non-overlapping ranges
  - `[(10, 20), (30, 40)]` → `[(10, 20), (30, 40)]`
- The Union shall return ranges sorted by start
- When input is empty, return empty list
- When input has single range, return that range

### Subtract

- The Subtract shall remove excluded regions from included ranges
  - `[(10, 80)]` minus `[(30, 40)]` → `[(10, 29), (41, 80)]`
- The Subtract shall handle exclusion at start
  - `[(10, 80)]` minus `[(10, 30)]` → `[(31, 80)]`
- The Subtract shall handle exclusion at end
  - `[(10, 80)]` minus `[(60, 80)]` → `[(10, 59)]`
- The Subtract shall handle full exclusion
  - `[(10, 80)]` minus `[(10, 80)]` → `[]`
- The Subtract shall handle exclusion larger than included
  - `[(20, 40)]` minus `[(10, 80)]` → `[]`
- The Subtract shall handle multiple exclusions
  - `[(10, 80)]` minus `[(20, 30), (50, 60)]` → `[(10, 19), (31, 49), (61, 80)]`
- The Subtract shall handle non-overlapping exclusions (no effect)
  - `[(10, 20)]` minus `[(30, 40)]` → `[(10, 20)]`
- When included is empty, return empty list
- When excluded is empty, return included unchanged

### Edge Cases

- The calculator shall handle single-line ranges correctly
  - `[(5, 5)]` is a valid single-line range
- The calculator shall handle ranges at line 1
- The calculator shall reject invalid ranges (Start > End) by treating as empty

### Performance

- Union shall be O(n log n) where n = number of input ranges
- Subtract shall be O(n * m) where n = included, m = excluded
  - Acceptable: typical usage has small n and m

## Constraints

- **Immutable operations** — input lists not modified; new lists returned
- **1-based lines** — consistent with RepoQL conventions
- **Inclusive ranges** — [10, 20] means lines 10 through 20 inclusive

## References

- [Line-Range Globbing Design](../../designs/future/globbing.md) — Algorithm Details section
- [LineRange in Registry Model](01-registry-model.md) — LineRange struct

## Error Policy

No errors expected — pure computation on value types. Invalid input (empty, malformed) handled gracefully with empty results.
