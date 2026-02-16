# Line-Range Globbing Design

## North Star

Select anything with one pattern—files, symbols, line ranges, or any combination. Exclude regions without losing the rest. Trust results reflect indexed reality.

## Context

Pattern matching against the URI registry with line-range precision. When you glob for symbols and exclude a line range, you get back exactly what remains—full symbols where unaffected, partial ranges where occluded.

**Enables:** [Pattern Matching Flow](../flows/future/globbing/pattern-matching.md)

**Built on:** [UriRegistry](../../src/RepoQL.Contracts/UriRegistry/UriRegistry.cs) — source of truth for files and symbols

## Constraints

- Registry-based — matches against in-memory registry, not database
- Line granularity — character offsets not supported (simplicity)
- Inclusive ranges — `#line=10,20` means lines 10 through 20 inclusive
- Immutable during match — snapshot semantics, no mid-operation updates
- Symbol spans required — registry must have span info for symbols

---

## Components

```
┌─────────────────────────────────────────────────────────────┐
│                        Callers                               │
│  glob_files UDF  |  read tool  |  search scope              │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼ MatchPattern(pattern)
┌─────────────────────────────────────────────────────────────┐
│                  UriRegistryExtensions                       │
│  - Parses pattern                                           │
│  - Collects candidates                                      │
│  - Delegates to calculator and simplifier                   │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│  UriRegistry    │  │ LineRangeCalc   │  │  UriSimplifier  │
│                 │  │                 │  │                 │
│  - Files        │  │  - Union        │  │  - To file URI  │
│  - Symbols      │  │  - Subtract     │  │  - To symbol URI│
│  - Spans        │  │  - Coalesce     │  │  - To line URI  │
└─────────────────┘  └─────────────────┘  └─────────────────┘
```

---

## Contracts

### SymbolEntry

```csharp
/// <summary>
/// Symbol metadata including location span.
/// </summary>
public record SymbolEntry(
    string Kind,       // "class", "method", "function", etc.
    int StartLine,     // 1-based, inclusive
    int EndLine);      // 1-based, inclusive
```

Replaces the current `string` value in `FileEntry.Symbols`.

### FileEntry (updated)

```csharp
public record FileEntry(
    UriStatus Status,
    DateTime? IndexedAt,
    string? Error,
    EmbeddingStatus EmbeddingStatus,
    int EmbeddedChunkCount,
    DateTime? EmbeddedAt,
    int LineCount,                                    // NEW: total lines in file
    IReadOnlyDictionary<RepoUri, SymbolEntry> Symbols // CHANGED: SymbolEntry not string
);
```

### LineRange

```csharp
/// <summary>
/// A contiguous range of lines within a file.
/// </summary>
public readonly record struct LineRange(
    int Start,    // 1-based, inclusive
    int End)      // 1-based, inclusive
{
    public bool Overlaps(LineRange other) =>
        Start <= other.End && other.Start <= End;

    public bool Contains(LineRange other) =>
        Start <= other.Start && other.End <= End;

    public int Length => End - Start + 1;
}
```

### ILineRangeCalculator

```csharp
/// <summary>
/// Set operations on line ranges within a single file.
/// </summary>
public interface ILineRangeCalculator
{
    /// <summary>
    /// Union multiple ranges, merging overlaps.
    /// </summary>
    IReadOnlyList<LineRange> Union(IEnumerable<LineRange> ranges);

    /// <summary>
    /// Subtract exclusions from included ranges.
    /// Returns remaining ranges after subtraction.
    /// </summary>
    IReadOnlyList<LineRange> Subtract(
        IReadOnlyList<LineRange> included,
        IReadOnlyList<LineRange> excluded);
}
```

**Implementation:** `LineRangeCalculator`
- Union: sort by start, merge overlapping/adjacent
- Subtract: for each included range, carve out excluded portions

### IUriSimplifier

```csharp
/// <summary>
/// Converts line ranges back to canonical URI form.
/// </summary>
public interface IUriSimplifier
{
    /// <summary>
    /// Simplify a line range to the most specific URI.
    /// </summary>
    /// <param name="fileUri">The containing file</param>
    /// <param name="range">The line range to simplify</param>
    /// <param name="entry">File entry with symbol spans</param>
    /// <returns>Simplified URI (file, symbol, or line range)</returns>
    RepoUri Simplify(RepoUri fileUri, LineRange range, FileEntry entry);
}
```

**Implementation:** `UriSimplifier`
- If range equals file's full range → return file URI (no fragment)
- If range exactly matches a symbol's span → return symbol URI
- Otherwise → return line range URI (`#line=N,M`)

---

## Data Flow

### MatchPattern

```
MatchPattern("src/**/*.cs#symbol=*;!#line=1,30")
    │
    ├─► Parse pattern
    │       positives = ["src/**/*.cs#symbol=*"]
    │       negatives = ["#line=1,30"]
    │
    ├─► For each file in registry matching "src/**/*.cs":
    │       │
    │       ├─► Expand to line ranges (from symbols)
    │       │       AuthService      → (10, 80)
    │       │       AuthService.Login → (25, 45)
    │       │       IAuthService     → (85, 95)
    │       │
    │       ├─► Union positive ranges
    │       │       [(10, 80), (25, 45), (85, 95)]
    │       │       → [(10, 80), (85, 95)]  (merged)
    │       │
    │       ├─► Subtract negative ranges
    │       │       [(10, 80), (85, 95)] - [(1, 30)]
    │       │       → [(31, 80), (85, 95)]
    │       │
    │       └─► Simplify each range
    │               (31, 80) → no exact match → #line=31,80
    │               (85, 95) → exact match   → #symbol=IAuthService
    │
    └─► Return URIs:
            file:///src/Auth.cs#line=31,80
            file:///src/Auth.cs#symbol=IAuthService
```

---

## Algorithm Details

### Union

```
Input:  [(10, 30), (25, 45), (50, 60), (55, 70)]
Sort:   [(10, 30), (25, 45), (50, 60), (55, 70)]
Merge:  [(10, 45), (50, 70)]

Algorithm:
1. Sort by start
2. For each range:
   - If overlaps or adjacent to current → extend current
   - Else → emit current, start new
```

### Subtract

```
Input:  included = [(10, 80)]
        excluded = [(30, 40)]

Result: [(10, 29), (41, 80)]

Algorithm:
For each included range:
  For each excluded range that overlaps:
    - If exclusion fully contains included → drop included
    - If exclusion splits included → emit two ranges
    - If exclusion overlaps start → trim start
    - If exclusion overlaps end → trim end
```

### Simplify

```
Range: (85, 95)
File:  LineCount = 200
Symbols:
  IAuthService: (85, 95)
  AuthService:  (10, 80)

Check order:
1. Range == (1, 200)? No → not file URI
2. Range matches any symbol exactly? Yes, IAuthService → symbol URI
3. Otherwise → line range URI
```

---

## Indexing Integration

### SetIndexed Update

```csharp
public void SetIndexed(
    RepoUri uri,
    int lineCount,
    IReadOnlyDictionary<RepoUri, SymbolEntry> symbols)
{
    AddOrUpdate(
        uri,
        _ => new FileEntry(
            Status: UriStatus.Indexed,
            IndexedAt: DateTime.UtcNow,
            Error: null,
            EmbeddingStatus: EmbeddingStatus.Pending,
            EmbeddedChunkCount: 0,
            EmbeddedAt: null,
            LineCount: lineCount,
            Symbols: symbols),
        (_, existing) => existing with
        {
            Status = UriStatus.Indexed,
            IndexedAt = DateTime.UtcNow,
            Error = null,
            LineCount = lineCount,
            Symbols = symbols
        });
}
```

### Parser Output

Parsers already emit spans. The commit pipeline maps span data to `SymbolEntry`:

```csharp
var symbols = records.Nodes
    .Where(n => n.Kind != "document")
    .ToDictionary(
        n => new RepoUri($"{fileUri}#symbol={n.QualifiedName}"),
        n => new SymbolEntry(
            Kind: n.Kind,
            StartLine: records.Spans.First(s => s.NodeId == n.Id).StartLine,
            EndLine: records.Spans.First(s => s.NodeId == n.Id).EndLine));

registry.SetIndexed(fileUri, lineCount, symbols);
```

---

## SQL Surface

### glob_files Enhancement

The existing `glob_files` macro can delegate to the registry-based implementation:

```sql
-- Current: queries node table
-- Enhanced: queries registry via UDF for line-range support

CREATE OR REPLACE MACRO glob_files(pattern_spec := NULL) AS TABLE
SELECT uri FROM _glob_files_internal(pattern_spec);
```

### UDF

```csharp
[StructuredUdf("_glob_files_internal")]
public IEnumerable<GlobResult> GlobFiles([UdfDefault("NULL")] string? pattern)
{
    foreach (var uri in _registry.MatchPattern(pattern))
    {
        yield return new GlobResult(uri.AbsoluteUri);
    }
}

public record GlobResult(string Uri);
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Invalid pattern syntax | Throw `ArgumentException` with details |
| File missing LineCount | Treat as unknown length; can't simplify to file URI |
| Symbol missing span | Skip symbol in expansion, log warning |
| Empty positive matches | Return empty (not error) |
| All ranges excluded | Return empty (not error) |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Line granularity | Character granularity | Simplicity; lines are natural code boundaries |
| Registry-based | Database-based | Real-time accuracy; no commit lag |
| Normalize to ranges | Handle each type separately | Uniform set operations; simpler algorithm |
| Eager simplification | Return raw ranges | Canonical URIs are more useful to callers |
| Snapshot semantics | Live updates | Predictable results during pattern match |

## Alternatives Considered

**Character-level precision:** Would enable finer exclusions but adds complexity. Lines are natural boundaries for code; character offsets would rarely be useful.

**Database-backed matching:** Could query `span` table directly. Rejected: introduces commit lag, loses registry's real-time accuracy.

**Lazy simplification:** Return line ranges, let caller simplify. Rejected: callers would need access to symbol spans; better to centralize.

**Set operations on URIs directly:** Match URIs as sets, handle overlaps. Rejected: URIs don't compose well; line ranges do.

## Risks

| Risk | Mitigation |
|------|------------|
| Large file with many symbols slows expansion | Acceptable: O(symbols) is small; profile if needed |
| Span data unavailable for some formats | Log warning, skip symbols; file-level glob still works |
| Memory for expanded ranges | Transient; garbage collected after match completes |

## Extension Points

- `ILineRangeCalculator` — injectable for testing
- `IUriSimplifier` — could add heuristics (e.g., "close enough" symbol matches)
- Pattern syntax — fragment types could be extended (`#kind=method`)
