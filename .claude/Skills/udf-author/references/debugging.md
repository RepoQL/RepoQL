# Debugging UDFs

What to do when a UDF doesn't work.

---

## Symptom: UDF Not Found

**Error**: `Catalog Error: Scalar Function with name "my_func" is not in the catalog`

**Causes and fixes**:

| Cause | Fix |
|-------|-----|
| Missing `[UdfClass]` on class | Add `[UdfClass]` attribute |
| Missing `[ScalarUdf]` on method | Add `[ScalarUdf("name")]` attribute |
| Class not in scanned assembly | Move to `UdfImplementations/` folder |
| Schema not initialized | Call `EnsureSchemaAsync()` first |
| Typo in function name | Check `Name` parameter in attribute |

**Diagnostic**:
```sql
-- List all registered functions
SELECT function_name FROM duckdb_functions()
WHERE function_name LIKE '%my_func%';
```

---

## Symptom: Macro Not Found

**Error**: `Catalog Error: Macro with name "my_macro" is not in the catalog`

**Causes and fixes**:

| Cause | Fix |
|-------|-----|
| `MacroName` not set | Add `MacroName = "my_macro"` to attribute |
| Macro generation failed | Check logs for SQL syntax errors |
| UDF registration failed first | Fix underlying UDF issue |

**Diagnostic**:
```sql
-- List all macros
SELECT macro_name FROM duckdb_macros()
WHERE macro_name LIKE '%my_macro%';

-- See macro definition
SELECT macro_definition FROM duckdb_macros()
WHERE macro_name = 'my_macro';
```

---

## Symptom: Wrong Number of Arguments

**Error**: `Binder Error: No function matches the given name and argument types`

**Causes**:
- Calling UDF directly instead of macro (macros handle defaults)
- Parameter count mismatch

**Fix**: Use the macro name, not the internal UDF name:
```sql
-- Wrong: calling internal UDF directly
SELECT _search_internal('query');

-- Right: calling macro with defaults
SELECT * FROM search('query');
```

---

## Symptom: NULL Results (NULL Propagation)

**UDF returns NULL unexpectedly**

**Most common cause**: DuckDB's default NULL propagation. When ANY scalar function argument is NULL, DuckDB returns NULL without calling the function at all. This is DuckDB's standard behavior for scalar functions.

**The framework handles this**: `GenerateMacro()` wraps all UDF arguments in `COALESCE(param::VARCHAR, '')`, converting NULL to empty string before it reaches the UDF. The UDF framework then handles empty-to-default conversion via `ApplyDefaults`. If you see NULL results, check that the macro is using COALESCE.

**Diagnostic**:
```sql
-- Call internal UDF directly to confirm NULL propagation
SELECT _my_func_internal('value', NULL, '10');  -- Returns NULL (skipped!)
SELECT _my_func_internal('value', '', '10');    -- Returns result (called!)

-- The generated macro should use COALESCE:
SELECT macro_definition FROM duckdb_macros() WHERE macro_name = 'my_func';
-- Should show: COALESCE(scope::VARCHAR, '')
```

**Debug steps**:

1. **Check input parsing**:
```csharp
[ScalarUdf("debug_input")]
public string DebugInput(string input)
{
    return $"Received: '{input}' (null={input is null})";
}
```
```sql
SELECT debug_input('test');
SELECT debug_input(NULL);
SELECT debug_input(some_column) FROM table;
```

2. **Check for silent exceptions**:
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
        _logger.LogError(ex, "UDF failed for input: {Input}", input);
        return $"ERROR: {ex.Message}";  // Return error instead of null
    }
}
```

3. **Check service injection**:
```csharp
[ScalarUdf("check_service")]
public string CheckService([UdfDefault("''")] string _)
{
    if (_myService is null) return "SERVICE_NULL";
    if (!_myService.IsEnabled) return "SERVICE_DISABLED";
    return "SERVICE_OK";
}
```

---

## Symptom: Structured UDF Returns Empty

**UDF returns no rows when it should**

**Debug steps**:

1. **Check raw JSON output**:
```sql
-- Call internal UDF directly to see JSON
SELECT _search_internal('query', '10');
-- Should return: [{"uri":"...","score":0.95},...]
```

2. **Check for `__udf_error__`**:
```sql
SELECT _search_internal('query', '10') LIKE '%__udf_error__%';
```

3. **Verify record properties are properties, not fields**:
```csharp
// Wrong: fields
public record SearchResult { public string Uri; }

// Right: properties
public record SearchResult(string Uri, double Score);
```

---

## Symptom: Type Conversion Errors

**Error in logs**: `FormatException: Input string was not in a correct format`

**Causes**:
- SQL passed unexpected type
- NULL not handled
- Locale-specific parsing (decimals with comma)

**Fix**: Defensive parsing:
```csharp
[ScalarUdf("safe_parse")]
public string SafeParse(string input)
{
    if (string.IsNullOrEmpty(input) || input == "NULL")
        return "0";

    if (!int.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        return "0";

    return (value * 2).ToString();
}
```

---

## Symptom: Macro Syntax Error

**Error**: `Parser Error: syntax error at or near ...`

**Common causes**:

1. **Missing quotes in string default**:
```csharp
// Wrong
[UdfDefault("hello")]

// Right
[UdfDefault("'hello'")]
```

2. **Reserved word as parameter name**:
```csharp
// Problematic
public string MyFunc(string order)  // ORDER is reserved

// Better
public string MyFunc(string sortOrder)
```

**Diagnostic**: Check generated macro SQL:
```csharp
// In test, inspect macro generation
var registry = new UdfRegistry(services, logger);
var sql = registry.GenerateMacrosSql();
Console.WriteLine(sql);  // Inspect for syntax issues
```

---

## Symptom: Performance Degradation

See `references/performance.md` for detailed guidance.

**Quick checks**:
```sql
-- Time the UDF
EXPLAIN ANALYZE SELECT my_func(column) FROM large_table;

-- Check if it's being called per-row
-- Look for high "Rows" count on the function operator
```

---

## Logging

### Enable UDF Logging

UDFs should inject `ILogger<T>`:

```csharp
[UdfClass]
public class MyUdf(ILogger<MyUdf> logger)
{
    [ScalarUdf("my_func")]
    public string MyFunc(string input)
    {
        logger.LogDebug("MyFunc called with: {Input}", input);
        // ...
    }
}
```

### Log Levels

| Level | Use For |
|-------|---------|
| `LogDebug` | Input/output tracing |
| `LogInformation` | Significant operations |
| `LogWarning` | Recoverable issues (service unavailable) |
| `LogError` | Exceptions, failures |

### Viewing Logs

In development with Aspire:
```sql
-- Check structured logs via Aspire dashboard
-- Or use mcp__aspire-dashboard__list_structured_logs
```

---

## Common Debugging Workflow

1. **Isolate**: Can you reproduce with a simple SQL query?
```sql
SELECT my_func('test_input');
```

2. **Check registration**: Is the UDF in the catalog?
```sql
SELECT * FROM duckdb_functions() WHERE function_name LIKE '%my_func%';
```

3. **Check raw output**: For structured UDFs, what's the JSON?
```sql
SELECT _my_func_internal('test');
```

4. **Add logging**: Inject logger, log inputs and outputs

5. **Unit test**: Extract logic to testable method, test in isolation

6. **Check types**: Are you parsing inputs correctly?

7. **Check nulls**: Is any input unexpectedly null?

---

## UDF Doesn't Update After Code Change

**Symptom**: Changed C# code but UDF behavior unchanged

**Cause**: UDFs register at schema initialization. Connection may be reusing old registration.

**Fix**:
1. Restart the host process
2. Or in development: use Aspire dashboard to restart
3. Or force schema re-initialization

```csharp
// In tests, create fresh data store
await using var dataStore = await CreateTestDataStore();
```

---

*When it breaks, isolate. When it's isolated, log. When it's logged, fix.*
