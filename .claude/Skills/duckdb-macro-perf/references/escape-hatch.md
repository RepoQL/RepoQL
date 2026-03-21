---
description: When and how to move from DuckDB TABLE macros to C# StructuredUdfs via IReentrantReader. The escape from CTE re-evaluation when SQL optimizations are exhausted.
zones: { K: 50, C: 20, P: 20, W: 10 }
---

# The Escape Hatch: SQL → C#

## When to Use

TABLE macros hit a wall when a pipeline needs materialized intermediate results. The symptoms:

- A CTE is referenced 4+ times and each reference re-evaluates expensive UDFs
- Nesting one TABLE macro inside another adds seconds of overhead
- You've applied all three macro fixes (cast, param, single-ref) and it's still slow
- The same logic as manual CTEs is 3x+ faster than the macro version

If you've exhausted the SQL-level fixes and the gap persists, the problem is architectural: TABLE macros can't materialize CTEs.

## How It Works

A `StructuredUdf` runs C# code inside a DuckDB UDF callback. `IReentrantReader` gives it a secondary read-only connection to execute SQL queries. Each query materializes as a `List<T>` — no CTE re-evaluation possible.

```
DuckDB query → calls StructuredUdf → C# code runs →
  → reader.Read("SELECT ... FROM macro1(...)") → List<T> (materialized)
  → reader.Read("SELECT ... FROM macro2(...)") → List<T> (materialized)
  → C# scoring/merging on in-memory data
  → returns IEnumerable<ResultRow> → JSON → json_each() → SQL rows
```

## The Pattern

```csharp
[UdfClass]
public sealed class MyPipelineUdf(IReentrantReader? reader = null)
{
    [StructuredUdf("_my_pipeline_internal", MacroName = "my_pipeline",
        Description = "Orchestrated pipeline with materialized intermediates")]
    public IEnumerable<ResultRow> Execute(
        string query,
        [UdfDefault("NULL")] string? scope)
    {
        var activeReader = reader ?? DuckDbDataStore.GetAmbientReentrantReader();
        if (activeReader is null) return [];

        // Phase 1: SQL queries (each materializes fully)
        var step1 = activeReader.Read(
            $"SELECT ... FROM _step1(q := '{Escape(query)}')",
            r => new Step1Row(r.GetGuid(0), r.GetDouble(1)));

        var step2 = activeReader.Read(
            $"SELECT ... FROM _step2(q := '{Escape(query)}')",
            r => new Step2Row(r.GetGuid(0), r.GetDouble(1)));

        // Phase 2: C# merge/score (pure memory)
        var merged = Merge(step1, step2);

        // Phase 3: Enrich (one more SQL call on small result set)
        var nodeIds = string.Join(",", merged.Select(m => $"'{m.Id}'::UUID"));
        var enriched = activeReader.Read(
            $"SELECT ... FROM node WHERE id IN ({nodeIds})", mapper);

        return BuildResults(merged, enriched);
    }

    private static string Escape(string v) => v.Replace("'", "''");
}
```

## Rules

- `IReentrantReader.Read` is synchronous — no async
- The reader uses a read-only connection — no writes
- SQL strings must escape single quotes (`'` → `''`)
- UDF runs on DuckDB worker threads — no thread-local state
- Return type is `IEnumerable<T>` where T is a record with public properties
- All columns become VARCHAR in DuckDB (the framework serializes to JSON)
- The generated macro wraps with `json_each()` — you may need a typed wrapper macro with `TRY_CAST` for downstream consumers expecting UUID/DOUBLE columns
- `JsonSerializer.Serialize(new { ... })` produces `{}` in IL-trimmed Release builds — use `JsonObject` instead

## When NOT to Use

- Simple queries with no CTE multi-reference — SQL is simpler and faster
- Queries that don't call UDFs — native DuckDB is efficient
- One-off diagnostic queries — the overhead of a C# UDF isn't worth it
- When the SQL macro is already fast enough (<1s)

The SQL surface is always preferred when it works. C# is the fallback when TABLE macro limitations make SQL untenable.

## Canonical Example

`SearchPipelineUdf.cs` — the search pipeline moved from a 400-line SQL macro with 5 CTE multi-references to a 926-line C# UDF. Same timing (~5s), but with evidence-based object scoring that wasn't possible in the SQL version due to CTE re-evaluation making every additional feature slower.
