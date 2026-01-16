---
name: udfAuthor
description: Create effective UDFs for RepoQL. Use when implementing new SQL functions, extending the query surface, or adding computational capabilities to the graph.
zones: { K: 50, P: 15, C: 25, W: 10 }
---

# UDF Author

UDFs bridge C# and SQL. The framework handles marshalling and registration; you provide logic and declare shape.

## Context

You're extending RepoQL's query surface with custom functionality. The UDF framework uses attributes for declaration and reflection for registration—no manual DuckDB calls needed.

## Decision: Scalar or Structured?

| Question | Scalar | Structured |
|----------|--------|------------|
| How many rows per input? | One | Zero to many |
| Return type | Single value | `IEnumerable<T>` |
| SQL usage | Expression | `FROM function()` |
| Attribute | `[ScalarUdf]` | `[StructuredUdf]` |

---

## Capsule: ScalarUdf

**Invariant**
A scalar UDF transforms input values 1:1. One row in, one value out.

**Example**
```csharp
[UdfClass]
public class TextUdf
{
    [ScalarUdf("word_count", IsPure = true)]
    public string Count(string text)
    {
        return text?.Split(' ').Length.ToString() ?? "0";
    }
}
```
```sql
SELECT word_count('hello world');  -- "2"
```
//BOUNDARY: If you need to return multiple rows, use StructuredUdf.

**Depth**
- All params and return are VARCHAR at DuckDB level (all-VARCHAR strategy)
- Framework parses to C# types, serializes results back to string
- `IsPure = true` enables constant folding optimization
- SeeAlso: `references/framework.md`

---

## Capsule: StructuredUdf

**Invariant**
A structured UDF returns a table. Zero to many rows per invocation.

**Example**
```csharp
[StructuredUdf("_search_internal", MacroName = "search")]
public IEnumerable<SearchResult> Search(string query, [UdfDefault("10")] int k)
{
    yield return new SearchResult("file:///a.cs", 0.95);
    yield return new SearchResult("file:///b.cs", 0.87);
}

public record SearchResult(string Uri, double Score);
```
```sql
SELECT * FROM search('authentication', k := 5);
```
//BOUNDARY: Record properties become snake_case columns. PascalCase `Uri` → `uri`.

**Depth**
- Returns JSON array; macro expands via `json_each()`
- Record properties map to columns automatically
- Named parameters via macro defaults
- SeeAlso: `references/framework.md`

---

## Capsule: MacroPattern

**Invariant**
Internal UDF + public macro separates implementation from interface.

**Example**
```csharp
[ScalarUdf("_tree_internal", MacroName = "tree", IsPure = true)]
public string FormatTree(string uris, [UdfDefault("false")] bool foldersOnly)
```
Generates:
```sql
CREATE MACRO tree(uris, foldersOnly := false) AS (
    _tree_internal(uris::VARCHAR, foldersOnly::VARCHAR)
);
```
//BOUNDARY: MacroName is optional. Without it, UDF is exposed directly by its Name.

**Depth**
- `_` prefix convention for internal UDFs
- Macro provides: named parameters, defaults, type coercion
- UDF provides: C# logic, service access
- SeeAlso: `references/patterns.md`

---

## Capsule: DependencyInjection

**Invariant**
UDF classes support constructor injection for service access.

**Example**
```csharp
[UdfClass]
public class EmbedUdf(IEmbeddingProvider? embeddings)
{
    [ScalarUdf("embed_text")]
    public string? Embed(string text)
    {
        if (embeddings is null) return null;
        var vector = embeddings.EmbedAsync(text, default).GetAwaiter().GetResult();
        return SerializeFloatArray(vector);
    }
}
```
//BOUNDARY: Services resolved via IServiceProvider. Nullable for optional dependencies.

**Depth**
- Constructor parameters resolved by `ActivatorUtilities.CreateInstance()`
- Falls back to parameterless constructor if no DI needed
- Access scoped services via `DuckDbDataStore.GetService<T>()`

---

## Quick Reference

| Attribute | Purpose | Key Properties |
|-----------|---------|----------------|
| `[UdfClass]` | Mark class for discovery | — |
| `[ScalarUdf]` | Single-value return | `Name`, `MacroName`, `IsPure`, `Description` |
| `[StructuredUdf]` | Table return | `Name`, `MacroName`, `Description` |
| `[UdfDefault]` | Parameter default | `SqlDefault` (string literal) |

## What Must Remain True

1. **All parameters are VARCHAR** — Framework handles type conversion
2. **DuckDB.NET requires ≥1 parameter** — Add dummy param if truly parameterless
3. **Exceptions cannot propagate** — Framework catches and serializes to JSON error
4. **Single-writer architecture** — UDFs should be read-only; mark `IsPure = true`
5. **≤4 direct params recommended** — 5+ triggers JSON packing strategy

## Boundaries

**This skill covers**: Creating UDFs for RepoQL's SQL surface

**Does not cover**:
- DuckDB internals → See `docs/duckdb/`
- Schema design → See `docs/Schema.md`
- Macro-only extensions → Create `.sql` files in `Schema/Macros/`

## References

### Core
- `references/framework.md` — Registration lifecycle, type mapping, error handling
- `references/patterns.md` — Common patterns with full examples
- `references/constraints.md` — Hard rules and their rationale

### Operations
- `references/testing.md` — Unit tests, integration tests, test checklist
- `references/debugging.md` — Troubleshooting UDF issues
- `references/performance.md` — Optimization, caching, avoiding pitfalls

---

*Declare the shape. The framework does the rest.*
