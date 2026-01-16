# UDF Constraints

Hard rules that cannot be violated. Deviation causes failures or corruption.

---

## Rule: Single-Writer Architecture

**Constraint**: UDFs must not write to the database.

**Rationale**: RepoQL enforces single-writer via `DuckDbDataStore`. All writes go through the indexing pipeline. UDFs execute during queries—concurrent writes would corrupt data.

**Enforcement**: Mark UDFs `IsPure = true`. If a UDF must write (rare), it must coordinate through `DuckDbDataStore`.

**Exception**: None for standard UDFs.

---

## Rule: Minimum One Parameter

**Constraint**: DuckDB.NET requires at least one parameter per UDF.

**Rationale**: DuckDB.NET's `RegisterScalarFunction<>` generic requires type parameters for args.

**Workaround**:
```csharp
[ScalarUdf("_status_internal", MacroName = "status")]
public string GetStatus([UdfDefault("''")] string? _unused)
{
    // Implementation ignores _unused
}
```

**Exception**: None. This is a DuckDB.NET limitation.

---

## Rule: Maximum Four Direct Parameters

**Constraint**: UDFs with >4 parameters use JSON packing.

**Rationale**: DuckDB.NET provides `RegisterScalarFunction<T1..T4, TResult>` overloads. Beyond 4, the framework packs remaining params as JSON in the 3rd position.

**Pattern for 5+ params**:
```csharp
// Framework handles this automatically
[ScalarUdf("_complex_func")]
public string ComplexFunc(
    string required1,
    string required2,
    string optionsJson)  // Contains remaining params as JSON
{
    using var doc = JsonDocument.Parse(optionsJson);
    // Extract param3, param4, param5...
}
```

**Exception**: Write a manual macro that packs params if you need finer control.

---

## Rule: Exceptions Cannot Propagate

**Constraint**: Never let exceptions escape UDF callbacks.

**Rationale**: UDFs run in unmanaged context (`[UnmanagedCallersOnly]`). Unhandled exceptions crash the process, not just the query.

**Framework handling**: Catches exceptions, serializes to JSON error format.

**Best practice**:
```csharp
[ScalarUdf("my_func")]
public string MyFunc(string input)
{
    try
    {
        // ... logic ...
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "UDF failed");
        return null;  // Or error JSON
    }
}
```

**Exception**: None. Always handle errors.

---

## Rule: IsPure Must Be Truthful

**Constraint**: Only mark `IsPure = true` for functions with no side effects.

**Rationale**: DuckDB uses purity for optimization—constant folding, expression caching, reordering. Lying causes incorrect results.

**Pure** (safe to mark true):
- String formatting
- Mathematical calculations
- Pattern matching
- Reading cached/indexed data

**Impure** (must mark false or omit):
- Network calls
- File I/O
- Random values
- Time-dependent results
- Anything with side effects

**Exception**: None. Incorrect purity declaration is a bug.

---

## Rule: Return Types Are Strings

**Constraint**: Scalar UDFs return `string` or `string?`. Structured UDFs return `IEnumerable<T>` where T is a record.

**Rationale**: All-VARCHAR strategy simplifies registration. Framework handles serialization.

**Returning numbers**:
```csharp
// Return as string, cast in SQL
[ScalarUdf("calculate")]
public string Calculate(string a, string b)
{
    var result = int.Parse(a) + int.Parse(b);
    return result.ToString();
}
```
```sql
SELECT calculate('1', '2')::INTEGER;  -- 3
```

**Exception**: None at the UDF level. Macros can cast results.

---

## Rule: Structured UDF Records Use Properties

**Constraint**: Record types for structured UDFs must use properties, not fields.

**Rationale**: Reflection in `UdfHelpers.GetColumnsFromType()` reads `GetProperties()`.

**Correct**:
```csharp
public record SearchResult(string Uri, double Score);  // ✓ Properties via primary constructor
public record SearchResult { public string Uri { get; init; } }  // ✓ Explicit properties
```

**Incorrect**:
```csharp
public record SearchResult { public string Uri; }  // ✗ Field, not property
```

**Exception**: None.

---

## Rule: Property Names Become Snake_Case

**Constraint**: Record property names are converted to snake_case for SQL columns.

**Rationale**: SQL convention. `UdfHelpers.ToSnakeCase()` handles conversion.

**Mapping**:
| C# Property | SQL Column |
|-------------|------------|
| `Uri` | `uri` |
| `IsValid` | `is_valid` |
| `HTTPStatus` | `httpstatus` |
| `XMLParser` | `xmlparser` |

**Implication**: Avoid consecutive capitals unless intentional. `IsHTTPError` → `is_httperror`.

**Exception**: None. Design property names accordingly.

---

## Rule: UdfDefault Uses SQL Literal Syntax

**Constraint**: `[UdfDefault]` value is a SQL literal, not C#.

**Rationale**: Value is inserted directly into generated macro SQL.

**Correct**:
```csharp
[UdfDefault("10")]           // Number
[UdfDefault("'hello'")]      // String (SQL quotes!)
[UdfDefault("NULL")]         // NULL
[UdfDefault("true")]         // Boolean
```

**Incorrect**:
```csharp
[UdfDefault("hello")]        // ✗ Missing quotes for string
[UdfDefault("null")]         // ✗ Lowercase null (SQL uses NULL)
```

**Exception**: None. Test your macros.

---

## Rule: Blocking Async Is Acceptable But Dangerous

**Constraint**: Async operations must block with `.GetAwaiter().GetResult()`.

**Rationale**: UDF callbacks are synchronous. Cannot return Task.

**Risk**: Blocking threads during query execution. Can cause deadlocks or timeouts.

**Mitigation**:
```csharp
[ScalarUdf("fetch")]
public string Fetch(string url)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        return _http.GetStringAsync(url, cts.Token)
            .GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
        return null;  // Timeout
    }
}
```

**Exception**: Prefer cached data. Avoid network calls per row.

---

## Rule: UDF Classes Must Be Discoverable

**Constraint**: UDF class must have `[UdfClass]` attribute and be in a scanned assembly.

**Rationale**: `UdfRegistry.DiscoverAndRegister()` scans for this attribute.

**Location**: Place in `UdfImplementations/` folder within `RepoQL.Data.DuckDB` project.

**Exception**: For UDFs in other assemblies, ensure assembly is passed to scanner.

---

## Summary Table

| Rule | Violation Consequence |
|------|----------------------|
| Single-writer | Data corruption |
| Min 1 param | Registration fails |
| Max 4 direct params | Must use JSON packing |
| Exceptions propagate | Process crash |
| IsPure lying | Incorrect results |
| Non-string return | Compilation error |
| Fields instead of properties | Missing columns |
| Wrong UdfDefault syntax | SQL syntax error |
| Unbounded async | Query timeout/deadlock |
| Missing [UdfClass] | UDF not discovered |

---

*These constraints protect the system. Respect them.*
