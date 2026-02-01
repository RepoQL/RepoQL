# Plan: Indexing Integration

Implements: [Line-Range Globbing Design](../../designs/future/globbing.md) — Indexing Integration section

## Scope

**Covers:**
- Commit pipeline updates to extract spans from `Records`
- Mapping parser output to `SymbolEntry` with spans
- Passing `lineCount` to `SetIndexed`
- Tests verifying spans are populated correctly

**Does not cover:**
- Registry model changes (Plan: Registry Model — prerequisite)
- Parser changes (parsers already emit spans)
- Line range calculations (Plan: Line Range Calculator)

## Enables

Once Indexing Integration exists:
- **Registry contains symbol spans** — pattern matching can expand symbols to line ranges
- **Registry contains file line counts** — simplification can detect whole-file matches
- **Plan: Pattern Matching** can proceed with real data

## Prerequisites

- Plan: Registry Model complete — `SymbolEntry`, updated `FileEntry`, `SetIndexed` signature

## North Star

Every indexed symbol has accurate span data. Line counts reflect actual file content.

## Done Criteria

### Span Extraction

- The commit pipeline shall extract spans from `Records.Spans` for each node
- The commit pipeline shall create `SymbolEntry` with `Kind` from node, `StartLine`/`EndLine` from span
  - When a node has multiple spans, use the first span (primary location)
  - When a node has no span, use `SymbolEntry(kind, 0, 0)` and log warning

### Line Count

- The commit pipeline shall determine line count from file content
  - When content available, count newlines + 1
  - When content unavailable, set to 0

### SetIndexed Call

- The commit pipeline shall call `SetIndexed(uri, lineCount, symbols)` with populated data
- The symbols dictionary shall include all non-document nodes with their spans

### Test Coverage

- The integration tests shall verify symbols have correct spans after indexing
  - Index a C# file with known structure
  - Query registry for symbol spans
  - Assert spans match expected line ranges
- The integration tests shall verify line count matches file

## Constraints

- **No parser changes** — parsers already emit spans; this plan only wires them to registry
- **Single span per symbol** — design uses primary span only; multi-span symbols use first
- **ReadOnly items included** — imported/embedded content gets spans too

## References

- [Line-Range Globbing Design](../../designs/future/globbing.md) — Indexing Integration section
- [Parsing Flow](../../flows/current/indexing/parsing.md) — Records structure with Spans
- [commit-batching.md](../../flows/current/indexing/commit-batching.md) — where SetIndexed is called

## Error Policy

- Missing span for node: log warning, use zero span, continue indexing
- Line count determination failure: use 0, continue indexing

Indexing must not fail due to span extraction issues — partial data is better than no data.
