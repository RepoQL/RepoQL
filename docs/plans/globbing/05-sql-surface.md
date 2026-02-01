# Plan: SQL Surface

Implements: [Line-Range Globbing Design](../../designs/future/globbing.md) — SQL Surface section

## Scope

**Covers:**
- `_glob_files_internal` UDF in `UriRegistryUdf`
- Update `glob_files` macro to use registry-based UDF
- Tests verifying SQL queries produce correct results

**Does not cover:**
- Registry model (Plan: Registry Model — prerequisite)
- Pattern matching logic (Plan: Pattern Matching — prerequisite)

## Enables

Once SQL Surface exists:
- **Agents can use line-range globbing from SQL** — `SELECT * FROM glob_files('src/**/*.cs#symbol=*;!#line=1,30')`
- **Read tool** can leverage the same functionality
- **Search scope** can use precise patterns

This is the final increment — makes the capability available to users.

## Prerequisites

- Plan: Registry Model complete
- Plan: Indexing Integration complete
- Plan: Line Range Calculator complete
- Plan: Pattern Matching complete

## North Star

Any pattern that works in code works identically in SQL. Zero translation layer issues.

## Done Criteria

### UDF Implementation

- The `UriRegistryUdf` shall implement `_glob_files_internal(pattern)`
- The UDF shall call `_registry.MatchPattern(pattern)`
- The UDF shall return results as table with `uri` column
- The UDF shall handle null/empty pattern (returns all files)

```csharp
[StructuredUdf("_glob_files_internal", Description = "Returns URIs matching pattern from registry")]
public IEnumerable<GlobResult> GlobFilesInternal([UdfDefault("NULL")] string? pattern)
{
    foreach (var uri in _registry.MatchPattern(pattern))
    {
        yield return new GlobResult(uri.AbsoluteUri);
    }
}

public record GlobResult(string Uri);
```

### Macro Update

- The `glob_files` macro shall delegate to `_glob_files_internal` for pattern-based queries
- When `uris` parameter provided, existing behavior preserved (list filtering)
- The macro shall preserve existing parameters (`ignore_case`, `default_scheme`)

### Backward Compatibility

- Existing `glob_files` queries shall produce same results
  - `SELECT * FROM glob_files('src/**/*.cs')` — files only
  - `SELECT * FROM glob_files('**/*.md')` — files only
- New fragment patterns shall work
  - `SELECT * FROM glob_files('src/**/*.cs#symbol=*')` — symbols
  - `SELECT * FROM glob_files('src/**/*.cs#symbol=*;!#line=1,30')` — with exclusion

### Integration Tests

- Test: Basic file pattern returns same results as before
- Test: Symbol pattern returns symbol URIs
- Test: Line range exclusion returns correct results
- Test: Scope readiness queries still work alongside glob_files

## Constraints

- **UDF calls registry** — no direct database queries in UDF
- **Macro preserves API** — existing queries must not break
- **Single UDF** — no variant UDFs; one entry point delegates to MatchPattern

## References

- [Line-Range Globbing Design](../../designs/future/globbing.md) — SQL Surface section
- [UriRegistryUdf.cs](../../../src/RepoQL.Data.DuckDB/UdfImplementations/UriRegistryUdf.cs) — existing UDFs
- [glob_files.sql](../../../src/RepoQL.Data.DuckDB/Schema/Macros/glob_files.sql) — current macro

## Error Policy

- Invalid pattern: UDF should propagate exception (SQL will report error)
- Registry unavailable: UDF should fail gracefully (should not happen in practice)

SQL surface is thin — error handling delegated to underlying MatchPattern implementation.
