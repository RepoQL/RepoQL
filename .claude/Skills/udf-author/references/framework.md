# UDF Framework Deep Dive

Technical details for the RepoQL UDF framework.

---

## Registration Lifecycle

```
DuckDbDataStore.EnsureSchema()
    │
    ▼
UdfRegistry.DiscoverAndRegister(connection)
    ├── Scan assemblies for [UdfClass]
    ├── For each class: resolve via DI
    ├── For each [ScalarUdf]: RegisterUdf{1-4}Params()
    └── For each [StructuredUdf]: RegisterStructuredUdf()
    │
    ▼
UdfRegistry.GenerateMacrosSql()
    └── CREATE MACRO for each UDF with MacroName
    │
    ▼
connection.Execute(macrosSql)
```

**Key files**:
- `UdfFramework/UdfRegistry.cs` — Discovery and registration engine
- `UdfFramework/Attributes.cs` — Attribute definitions
- `UdfFramework/UdfHelpers.cs` — Serialization utilities
- `DuckDbDataStore.cs:875-881` — Initialization call site

---

## Capsule: AllVarcharStrategy

**Invariant**
All parameters pass through DuckDB as VARCHAR. C# handles parsing.

**Example**
```csharp
// DuckDB signature: (VARCHAR, VARCHAR, VARCHAR) → VARCHAR
[ScalarUdf("_my_func")]
public string MyFunc(string text, int count, bool flag)
```
DuckDB passes `"hello"`, `"10"`, `"true"` → Framework parses to `string`, `int`, `bool`.

//BOUNDARY: Return type is always string. For structured UDFs, JSON array string.

**Depth**
- Simplifies registration (one type signature)
- Enables JSON for complex objects
- `UdfRegistry.ConvertStringValue()` handles: `int`, `long`, `double`, `bool`, `string`
- NULL handling: DuckDB propagates NULL (skips function call entirely). Generated macros COALESCE all args to `''` to prevent this. Empty string → `ConvertStringValue` → null for value types → `ApplyDefaults` applies `[UdfDefault]`

---

## Type Mapping

### Input Parameters

| C# Type | DuckDB Receives | Parsing |
|---------|-----------------|---------|
| `string` | VARCHAR | Direct |
| `int` | VARCHAR | `int.Parse()` |
| `long` | VARCHAR | `long.Parse()` |
| `double` | VARCHAR | `double.Parse()` |
| `bool` | VARCHAR | `bool.Parse()` |
| `string?` | VARCHAR (nullable) | Direct, NULL → null |

### Return Values

| UDF Type | C# Return | DuckDB Gets |
|----------|-----------|-------------|
| Scalar | `string` | VARCHAR |
| Scalar | `string?` | VARCHAR (nullable) |
| Structured | `IEnumerable<T>` | VARCHAR (JSON array) |

### Structured UDF Column Mapping

Record properties → JSON keys → SQL columns:

```csharp
public record SearchResult(string Uri, double Score, bool IsExact);
```
Becomes JSON:
```json
{"uri": "...", "score": 0.95, "is_exact": true}
```
Macro extracts as:
```sql
j.value->>'uri' AS uri,
(j.value->>'score')::DOUBLE AS score,
(j.value->>'is_exact')::BOOLEAN AS is_exact
```

**Naming**: `PascalCase` → `snake_case` via `UdfHelpers.ToSnakeCase()`

---

## Capsule: ParameterLimits

**Invariant**
DuckDB.NET supports 1-4 direct parameters. Beyond 4, use JSON packing.

**Example**
```csharp
// 4 params: direct registration
[ScalarUdf("_func4")]
public string Func4(string a, string b, string c, string d)

// 5+ params: first 2 direct, 3rd is JSON object containing rest
[ScalarUdf("_func5")]
public string Func5(string a, string b, string optionsJson)
// optionsJson = {"c": "...", "d": "...", "e": "..."}
```
//BOUNDARY: The framework auto-routes to `RegisterUdf3ParamsWithJson()` for 5+ params.

**Depth**
- Macro unpacks: `json_object('c', c, 'd', d, 'e', e)` passed as 3rd param
- C# uses `JsonDocument.Parse(optionsJson)` to extract
- See `UdfHelpers.ParseJsonOption<T>()` for typed extraction

---

## Capsule: DefaultParameters

**Invariant**
`[UdfDefault]` provides SQL-level defaults. Value is a SQL literal string.

**Example**
```csharp
[ScalarUdf("_search", MacroName = "search")]
public string Search(
    string query,
    [UdfDefault("10")] int k,
    [UdfDefault("'Find'")] string intent,  // Note: SQL string literal
    [UdfDefault("NULL")] string? scope)
```
Generates macro:
```sql
CREATE MACRO search(query, k := 10, intent := 'Find', scope := NULL) AS (...)
```
//BOUNDARY: SQL strings need quotes: `"'Find'"`. Numbers/NULL are bare: `"10"`, `"NULL"`.

**Depth**
- Applied by `UdfRegistry.ApplyDefaults()` when parameter is null
- Order: DuckDB value → `[UdfDefault]` → type default
- Common defaults: `"NULL"`, `"10"`, `"'default_string'"`, `"false"`

---

## Error Handling

### The Problem

UDF callbacks run in unmanaged context. Exceptions cannot propagate to SQL.

### The Solution

Framework catches exceptions and serializes to JSON:

```csharp
catch (Exception ex)
{
    var errorJson = $"[{{\"__udf_error__\":\"{EscapeJsonString(ex.Message)}\"}}]";
    writer.WriteValue(errorJson, i);
}
```

### Consuming Errors

Macros can detect and handle:
```sql
SELECT
    CASE WHEN json_extract(result, '$[0].__udf_error__') IS NOT NULL
         THEN 'Error: ' || json_extract(result, '$[0].__udf_error__')
         ELSE result
    END
FROM ...
```

### Best Practices

1. Validate inputs early with clear messages
2. Return null for expected empty cases (not exceptions)
3. Log errors via injected `ILogger<T>`
4. Consider returning error info as part of result schema

---

## Capsule: PureFunctions

**Invariant**
`IsPure = true` declares the function has no side effects. Enables optimization.

**Example**
```csharp
[ScalarUdf("format_uri", IsPure = true)]  // ✓ Pure
public string FormatUri(string uri) { ... }

[ScalarUdf("fetch_data", IsPure = false)]  // ✗ Has side effects
public string FetchData(string url) { ... }
```
//BOUNDARY: Lying about purity causes incorrect results. DuckDB may cache or reorder calls.

**Depth**
- Pure functions: same input → same output, no side effects
- DuckDB optimizations: constant folding, expression caching
- Most RepoQL UDFs should be pure (reading indexed data)
- Non-pure: network calls, random values, time-dependent

---

## Macro Generation

### Scalar UDF Macro

```csharp
[ScalarUdf("_word_count", MacroName = "word_count", IsPure = true)]
public string Count(string text, [UdfDefault("' '")] string delimiter)
```
Generates:
```sql
CREATE OR REPLACE MACRO word_count(text, delimiter := ' ') AS (
    _word_count(text::VARCHAR, delimiter::VARCHAR)
);
```

### Structured UDF Macro

```csharp
[StructuredUdf("_search_internal", MacroName = "search")]
public IEnumerable<SearchResult> Search(string q, [UdfDefault("10")] int k)

public record SearchResult(string Uri, double Score);
```
Generates:
```sql
CREATE OR REPLACE MACRO search(q, k := 10) AS TABLE (
    SELECT
        j.value->>'uri' AS uri,
        (j.value->>'score')::DOUBLE AS score
    FROM json_each(_search_internal(q::VARCHAR, k::VARCHAR)) AS j
    WHERE j.type = 'OBJECT'
);
```

### Column Type Inference

| C# Type | SQL Cast |
|---------|----------|
| `string` | No cast (already VARCHAR) |
| `int`, `long` | `::BIGINT` |
| `double` | `::DOUBLE` |
| `bool` | `::BOOLEAN` |

---

## Dependency Injection

### Constructor Injection

```csharp
[UdfClass]
public class MyUdf(IMyService service, ILogger<MyUdf> logger)
{
    [ScalarUdf("my_func")]
    public string MyFunc(string input)
    {
        logger.LogDebug("Processing: {Input}", input);
        return service.Process(input);
    }
}
```

### Resolution

1. `UdfRegistry` receives `IServiceProvider` at construction
2. `CreateUdfInstance()` uses `ActivatorUtilities.CreateInstance()`
3. Falls back to `Activator.CreateInstance()` for parameterless

### Service Scope

For scoped services during query execution:
```csharp
var result = dataStore.WithScope(scope =>
{
    var service = scope.ServiceProvider.GetService<IScopedService>();
    // ... use service
});
```

---

## File Locations

| File | Purpose |
|------|---------|
| `UdfFramework/Attributes.cs` | `[UdfClass]`, `[ScalarUdf]`, `[StructuredUdf]`, `[UdfDefault]` |
| `UdfFramework/UdfRegistry.cs` | Discovery, registration, macro generation |
| `UdfFramework/UdfHelpers.cs` | JSON serialization, type conversion |
| `UdfImplementations/` | All UDF classes (~25 implementations) |
| `Schema/Macros/` | Manual SQL macro files |

---

*The framework is the bridge. Declare your intent; it handles the translation.*
