# Plan: Registry Model Update

Implements: [Line-Range Globbing Design](../../designs/future/globbing.md) — Data structures

## Scope

**Covers:**
- `SymbolEntry` record with kind and span
- `FileEntry` update to use `SymbolEntry` and add `LineCount`
- `LineRange` value type for range operations
- Update all `SetIndexed` call sites to use new signature

**Does not cover:**
- Populating span data from parsers (Plan: Indexing Integration)
- Line range calculations (Plan: Line Range Calculator)
- Pattern matching changes (Plan: Pattern Matching)

## Enables

Once Registry Model exists:
- **Plan: Indexing Integration** can populate symbol spans
- **Plan: Line Range Calculator** has `LineRange` to operate on
- **Plan: Pattern Matching** can access symbol spans for expansion
- Existing functionality continues working (backward compatible defaults)

This is the foundation. All other globbing plans depend on it.

## Prerequisites

- None — this is the first increment

## North Star

Symbol locations available in registry without database queries. Zero regression in existing functionality.

## Done Criteria

### SymbolEntry

- The `SymbolEntry` record shall contain `Kind`, `StartLine`, and `EndLine`
- The `StartLine` and `EndLine` shall be 1-based inclusive
- The `SymbolEntry` shall be immutable (record type)

### FileEntry

- The `FileEntry` shall include a `LineCount` property (int)
- The `FileEntry.Symbols` shall be `IReadOnlyDictionary<RepoUri, SymbolEntry>`
- The `FileEntry` shall provide backward-compatible factory methods
  - When `LineCount` unknown, default to 0
  - When symbol has no span, default to `SymbolEntry(kind, 0, 0)`

### LineRange

- The `LineRange` shall be a readonly record struct with `Start` and `End`
- The `LineRange` shall provide `Overlaps(LineRange other)` method
- The `LineRange` shall provide `Contains(LineRange other)` method
- The `LineRange` shall provide `Length` property (End - Start + 1)
- When `Start > End`, the `LineRange` shall be considered empty

### SetIndexed

- The `SetIndexed` method shall accept `lineCount` parameter
- The `SetIndexed` method shall accept `IReadOnlyDictionary<RepoUri, SymbolEntry>` for symbols
- When called with old signature (if overload retained), shall use defaults

### Backward Compatibility

- The existing `UriRegistryExtensions.MatchPattern` shall continue working
  - When symbols have zero spans, pattern matching shall skip symbol expansion
- The existing `UriRegistryUdf` methods shall continue working
- All existing tests shall pass without modification

## Constraints

- **Immutable records** — design specifies thread-safe reads via immutability
- **1-based lines** — consistent with RepoQL span conventions
- **No breaking changes** — existing callers must not break

## References

- [Line-Range Globbing Design](../../designs/future/globbing.md) — contracts section
- [FileEntry.cs](../../../src/RepoQL.Contracts/UriRegistry/FileEntry.cs) — current implementation
- [UriRegistry.cs](../../../src/RepoQL.Contracts/UriRegistry/UriRegistry.cs) — SetIndexed method

## Error Policy

No runtime errors expected — this is pure data structure changes. Compile errors from signature changes should be fixed by updating call sites to use defaults.
