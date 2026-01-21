# UDF Patterns

Common patterns for RepoQL UDFs with complete, working examples.

---

## Pattern: Simple Scalar UDF

**Use for**: Pure transformations, formatting, parsing.

```csharp
[UdfClass]
public class FormatUdf
{
    [ScalarUdf("format_bytes", IsPure = true,
        Description = "Format byte count as human-readable string")]
    public string FormatBytes(string bytesStr)
    {
        if (!long.TryParse(bytesStr, out var bytes)) return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unitIndex = 0;
        double size = bytes;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F1} {units[unitIndex]}";
    }
}
```

**SQL usage**:
```sql
SELECT format_bytes(file_size) FROM artifacts WHERE file_size > 0;
-- "1.5 MB", "256.0 KB", etc.
```

---

## Pattern: Scalar with Optional Parameters

**Use for**: Functions with sensible defaults.

```csharp
[UdfClass]
public class TruncateUdf
{
    [ScalarUdf("_truncate_internal", MacroName = "truncate_text", IsPure = true)]
    public string Truncate(
        string text,
        [UdfDefault("100")] int maxLength,
        [UdfDefault("'...'")] string suffix)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? "";

        return text[..(maxLength - suffix.Length)] + suffix;
    }
}
```

**SQL usage**:
```sql
SELECT truncate_text(description);                    -- max 100, suffix "..."
SELECT truncate_text(description, 50);                -- max 50, suffix "..."
SELECT truncate_text(description, 50, ' [more]');     -- custom suffix
SELECT truncate_text(description, maxLength := 200);  -- named parameter
```

---

## Pattern: Structured UDF with Record

**Use for**: Returning multiple columns, table-valued functions.

```csharp
[UdfClass]
public class ParseUriUdf
{
    [StructuredUdf("_parse_uri_internal", MacroName = "parse_uri",
        Description = "Parse URI into components")]
    public IEnumerable<UriComponents> Parse(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            yield break;  // Returns empty table

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            yield return new UriComponents(uri, null, null, null, null);
            yield break;
        }

        yield return new UriComponents(
            Original: uri,
            Scheme: parsed.Scheme,
            Host: parsed.Host,
            Path: parsed.AbsolutePath,
            Fragment: string.IsNullOrEmpty(parsed.Fragment) ? null : parsed.Fragment
        );
    }

    public record UriComponents(
        string Original,
        string? Scheme,
        string? Host,
        string? Path,
        string? Fragment
    );
}
```

**SQL usage**:
```sql
SELECT * FROM parse_uri('https://example.com/path#section');
-- original | scheme | host        | path   | fragment
-- ...      | https  | example.com | /path  | #section

-- Join with other data
SELECT f.uri, p.scheme, p.host
FROM Files f, LATERAL parse_uri(f.uri) p
WHERE p.scheme = 'file';
```

---

## Pattern: Structured UDF Returning Multiple Rows

**Use for**: Splitting, searching, expanding data.

```csharp
[UdfClass]
public class SplitPathUdf
{
    [StructuredUdf("_split_path_internal", MacroName = "split_path")]
    public IEnumerable<PathSegment> Split(string path)
    {
        if (string.IsNullOrEmpty(path))
            yield break;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = "";

        for (int i = 0; i < segments.Length; i++)
        {
            accumulated += "/" + segments[i];
            yield return new PathSegment(
                Depth: i,
                Segment: segments[i],
                FullPath: accumulated
            );
        }
    }

    public record PathSegment(int Depth, string Segment, string FullPath);
}
```

**SQL usage**:
```sql
SELECT * FROM split_path('/src/components/Button.tsx');
-- depth | segment    | full_path
-- 0     | src        | /src
-- 1     | components | /src/components
-- 2     | Button.tsx | /src/components/Button.tsx
```

---

## Pattern: UDF with Service Injection

**Use for**: Accessing application services, embeddings, LLMs.

```csharp
[UdfClass]
public class SemanticSearchUdf(
    IEmbeddingProvider? embeddings,
    ILogger<SemanticSearchUdf> logger)
{
    [StructuredUdf("_semantic_search_internal", MacroName = "semantic_search")]
    public IEnumerable<SearchHit> Search(
        string query,
        [UdfDefault("10")] int k,
        [UdfDefault("NULL")] string? scope)
    {
        if (embeddings is null || !embeddings.Enabled)
        {
            logger.LogWarning("Embeddings not available");
            yield break;
        }

        var queryVector = embeddings.EmbedAsync(query, default)
            .GetAwaiter().GetResult();

        // ... perform vector search ...

        foreach (var result in results.Take(k))
        {
            yield return new SearchHit(result.Uri, result.Score);
        }
    }

    public record SearchHit(string Uri, double Score);
}
```

**SQL usage**:
```sql
SELECT * FROM semantic_search('authentication flow', k := 5);
SELECT * FROM semantic_search('error handling', scope := 'file:///src/**');
```

---

## Pattern: Glob/Pattern Matching

**Use for**: Filtering by patterns, path matching.

```csharp
[UdfClass]
public class GlobUdf
{
    [ScalarUdf("matches_glob", IsPure = true)]
    public string MatchesGlob(string uri, string pattern)
    {
        if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(pattern))
            return "false";

        // Convert glob to regex
        var regex = GlobToRegex(pattern);
        return Regex.IsMatch(uri, regex, RegexOptions.IgnoreCase)
            ? "true"
            : "false";
    }

    private static string GlobToRegex(string glob)
    {
        var escaped = Regex.Escape(glob);
        return "^" + escaped
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".") + "$";
    }
}
```

**SQL usage**:
```sql
SELECT * FROM Files WHERE matches_glob(uri, 'file:///src/**/*.cs') = 'true';
```

---

## Pattern: JSON Input for Complex Parameters

**Use for**: Functions with many optional parameters, complex configuration.

```csharp
[UdfClass]
public class QueryUdf
{
    [ScalarUdf("_query_internal")]
    public string Query(string sql, string optionsJson)
    {
        var options = ParseOptions(optionsJson);

        // Use options.MaxRows, options.Timeout, etc.
        // ...
    }

    private QueryOptions ParseOptions(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "NULL")
            return new QueryOptions();

        using var doc = JsonDocument.Parse(json);
        return new QueryOptions
        {
            MaxRows = doc.RootElement.TryGetProperty("max_rows", out var mr)
                ? mr.GetInt32() : 100,
            Timeout = doc.RootElement.TryGetProperty("timeout", out var t)
                ? t.GetInt32() : 30000
        };
    }

    private class QueryOptions
    {
        public int MaxRows { get; init; } = 100;
        public int Timeout { get; init; } = 30000;
    }
}
```

**SQL Macro** (manual, in `Schema/Macros/query.sql`):
```sql
CREATE OR REPLACE MACRO query(sql, max_rows := 100, timeout := 30000) AS (
    _query_internal(
        sql,
        json_object('max_rows', max_rows, 'timeout', timeout)
    )
);
```

---

## Pattern: Parameterless UDF (Workaround)

**Use for**: Status checks, environment info, current state.

```csharp
[UdfClass]
public class StatusUdf(IEmbeddingProvider? embeddings)
{
    // DuckDB.NET requires ≥1 param; use dummy
    [ScalarUdf("_embed_status_internal", MacroName = "embed_status")]
    public string GetStatus([UdfDefault("''")] string? _unused)
    {
        if (embeddings is null) return "disabled";
        if (!embeddings.Enabled) return "not_configured";
        return "ready";
    }
}
```

**SQL usage**:
```sql
SELECT embed_status();  -- "ready", "disabled", or "not_configured"
```

---

## Pattern: Async Operation (Blocking)

**Use for**: Network calls, I/O operations in UDFs.

```csharp
[UdfClass]
public class FetchUdf(HttpClient http)
{
    [ScalarUdf("fetch_json", IsPure = false)]  // Not pure: network call
    public string? FetchJson(string url)
    {
        try
        {
            // Block on async - UDFs are synchronous
            var response = http.GetStringAsync(url)
                .GetAwaiter().GetResult();
            return response;
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"{UdfHelpers.EscapeJsonString(ex.Message)}\"}}";
        }
    }
}
```

**Caution**: Blocking on async in UDFs. Keep operations fast or use timeouts.

---

## Anti-Patterns

### Don't: Return Complex Objects Directly

```csharp
// BAD: Returns object, not string
[ScalarUdf("get_data")]
public MyData GetData(string id) { ... }

// GOOD: Return JSON string
[ScalarUdf("get_data")]
public string GetData(string id)
{
    var data = FetchData(id);
    return JsonSerializer.Serialize(data);
}
```

### Don't: Ignore Null Handling

```csharp
// BAD: Will throw on null input
[ScalarUdf("process")]
public string Process(string input)
{
    return input.ToUpper();  // NullReferenceException!
}

// GOOD: Handle nulls explicitly
[ScalarUdf("process")]
public string? Process(string? input)
{
    return input?.ToUpper();
}
```

### Don't: Heavy Computation Per Row

```csharp
// BAD: Expensive operation per row
[ScalarUdf("analyze")]
public string Analyze(string text)
{
    var model = LoadMlModel();  // Loaded 2048 times per chunk!
    return model.Predict(text);
}

// GOOD: Cache expensive resources
[UdfClass]
public class AnalyzeUdf
{
    private readonly Lazy<MlModel> _model = new(() => LoadMlModel());

    [ScalarUdf("analyze")]
    public string Analyze(string text)
    {
        return _model.Value.Predict(text);
    }
}
```

---

*Study these patterns. Adapt them to your needs.*
