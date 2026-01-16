# UDF Performance

Optimizing UDFs for efficient execution.

---

## Capsule: VectorizedExecution

**Invariant**
UDFs are called once per chunk (up to 2048 rows), not once per row. Design accordingly.

**Example**
```csharp
// The framework calls this once with n values (n ≤ 2048)
// NOT 2048 separate calls
(readers, writer, n) => {
    for (ulong i = 0; i < n; i++) {
        var input = readers[0].GetValue<string>(i);
        var result = Process(input);
        writer.WriteValue(result, i);
    }
}
```
//BOUNDARY: Your C# method is wrapped. You see one call per invocation, but expensive setup runs per-chunk.

**Depth**
- Chunk size: `STANDARD_VECTOR_SIZE` = 2048
- For 1M rows: ~489 chunk invocations
- Expensive initialization should be outside the loop or cached

---

## Capsule: CacheExpensiveResources

**Invariant**
Resources loaded once should not be loaded per-chunk or per-row.

**Example**
```csharp
// BAD: Loads model 489 times for 1M rows
[ScalarUdf("classify")]
public string Classify(string text)
{
    var model = LoadModel();  // Expensive!
    return model.Predict(text);
}

// GOOD: Load once, reuse
[UdfClass]
public class ClassifyUdf
{
    private readonly Lazy<Model> _model = new(() => LoadModel());

    [ScalarUdf("classify")]
    public string Classify(string text)
    {
        return _model.Value.Predict(text);
    }
}
```
//BOUNDARY: UDF class instances are reused. Instance fields persist across chunks.

**Depth**
- Use `Lazy<T>` for thread-safe lazy initialization
- Inject expensive services via DI (they're created once)
- Cache regex patterns: `private static readonly Regex _pattern = new(..., Compiled)`

---

## Capsule: BatchNetworkCalls

**Invariant**
Network calls per-row multiply latency. Batch when possible.

**Example**
```csharp
// BAD: 2048 network calls per chunk
[ScalarUdf("fetch_metadata")]
public string FetchMetadata(string uri)
{
    return _http.GetStringAsync(uri).GetAwaiter().GetResult();
}

// BETTER: If you must call per-row, add timeout
[ScalarUdf("fetch_metadata")]
public string FetchMetadata(string uri)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    try {
        return _http.GetStringAsync(uri, cts.Token).GetAwaiter().GetResult();
    } catch {
        return null;
    }
}

// BEST: Design to avoid per-row network calls
// Use structured UDF to batch:
[StructuredUdf("fetch_metadata_batch")]
public IEnumerable<Metadata> FetchBatch(string urisJson)
{
    var uris = JsonSerializer.Deserialize<string[]>(urisJson);
    var tasks = uris.Select(u => _http.GetStringAsync(u));
    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
    // ... return results
}
```
//BOUNDARY: Network latency dominates. 100ms × 2048 = 3+ minutes per chunk.

---

## Capsule: AvoidAllocation

**Invariant**
Allocations in hot loops trigger GC pressure. Reuse buffers.

**Example**
```csharp
// BAD: Allocates StringBuilder per row
for (ulong i = 0; i < n; i++)
{
    var sb = new StringBuilder();  // Allocation!
    // ... use sb ...
    writer.WriteValue(sb.ToString(), i);
}

// GOOD: Reuse outside loop
var sb = new StringBuilder();
for (ulong i = 0; i < n; i++)
{
    sb.Clear();
    // ... use sb ...
    writer.WriteValue(sb.ToString(), i);
}
```
//BOUNDARY: `string` results still allocate. Focus on intermediate objects.

**Depth**
- `List<T>`, `StringBuilder`, `Dictionary<K,V>` — reuse with `.Clear()`
- Arrays — use `ArrayPool<T>.Shared`
- Regex — compile once as static field
- Small value types — avoid boxing

---

## Profiling UDFs

### Using EXPLAIN ANALYZE

```sql
EXPLAIN ANALYZE
SELECT my_udf(column) FROM large_table;
```

Look for:
- **Time** on the UDF operator
- **Rows** processed
- **Cardinality** estimates vs actual

### Benchmarking in C#

```csharp
[Test]
public void Benchmark_MyUdf()
{
    var udf = new MyUdf();
    var inputs = Enumerable.Range(0, 10000).Select(i => $"input_{i}").ToArray();

    var sw = Stopwatch.StartNew();
    foreach (var input in inputs)
    {
        udf.MyFunc(input);
    }
    sw.Stop();

    Console.WriteLine($"10K calls: {sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"Per call: {sw.ElapsedMilliseconds / 10000.0}ms");
}
```

### Red Flags

| Metric | Concern |
|--------|---------|
| > 1ms per row | Too slow for large tables |
| Growing memory | Memory leak (allocation without release) |
| GC pauses | Too many allocations |
| Network in profile | Per-row network calls |

---

## Performance Guidelines

### Do

| Practice | Benefit |
|----------|---------|
| Mark pure functions `IsPure = true` | Enables constant folding |
| Cache compiled regex | Avoid recompilation |
| Use `Lazy<T>` for expensive init | One-time cost |
| Inject services via DI | Reuse across calls |
| Use `StringBuilder` pool | Reduce allocations |
| Add timeouts to network calls | Bound worst case |

### Don't

| Anti-Pattern | Cost |
|--------------|------|
| Load resources per-chunk | N × load time |
| Network call per-row | Latency explosion |
| Allocate in loop | GC pressure |
| Use reflection per-row | Slow path |
| Parse JSON per-row without caching | Repeated work |
| Create regex per-call | Compilation overhead |

---

## Capsule: PureOptimization

**Invariant**
`IsPure = true` enables DuckDB to optimize: constant folding, caching, reordering.

**Example**
```sql
-- With IsPure = true, DuckDB evaluates once:
SELECT * FROM t WHERE format_bytes('1048576') = '1.0 MB';
-- Becomes: SELECT * FROM t WHERE '1.0 MB' = '1.0 MB';
-- Becomes: SELECT * FROM t WHERE true;

-- Without IsPure, called for every row scan
```
//BOUNDARY: Only mark pure if truly side-effect free. Lying causes incorrect results.

---

## Structured UDF Performance

### JSON Serialization Cost

Structured UDFs serialize to JSON. For large result sets:

```csharp
// Potentially slow: large result set
[StructuredUdf("get_all_files")]
public IEnumerable<FileInfo> GetAllFiles(string scope)
{
    return _index.GetAllFiles(scope);  // Could be 100K+ rows
}
```

**Mitigations**:
- Add `k` parameter to limit results
- Use pagination (offset/limit pattern)
- Return IDs, not full objects

### Macro Expansion Cost

`json_each()` has overhead. For simple cases, consider scalar UDF returning delimited string:

```csharp
// For simple lists, sometimes faster:
[ScalarUdf("get_tags")]
public string GetTags(string uri)
{
    return string.Join(",", GetTagsForUri(uri));
}
```
```sql
SELECT unnest(string_split(get_tags(uri), ',')) AS tag FROM files;
```

---

## Query-Level Optimization

### Filter Before UDF

```sql
-- BAD: UDF called for all rows, then filtered
SELECT my_expensive_udf(content) FROM files WHERE lang = 'csharp';

-- BETTER: Filter first (if UDF result not needed in filter)
WITH filtered AS (
    SELECT content FROM files WHERE lang = 'csharp'
)
SELECT my_expensive_udf(content) FROM filtered;
```

### Limit Results

```sql
-- BAD: Process all, take 10
SELECT * FROM (
    SELECT my_udf(x) as result FROM large_table
) LIMIT 10;

-- BETTER: If possible, push limit into data source
SELECT my_udf(x) FROM large_table LIMIT 10;
```

### Parallelize with Morsel-Driven Execution

DuckDB parallelizes across row groups automatically. UDFs benefit if:
- They're CPU-bound (not I/O-bound)
- They don't have shared mutable state
- They're marked `IsPure = true`

---

## Performance Checklist

Before shipping a UDF intended for large-scale use:

- [ ] Expensive resources cached (not loaded per-chunk)
- [ ] No network calls per-row (or batched with timeout)
- [ ] Allocations minimized in hot loop
- [ ] `IsPure = true` if side-effect free
- [ ] Profiled with EXPLAIN ANALYZE on realistic data
- [ ] Benchmarked C# method in isolation
- [ ] Structured UDF results bounded (k parameter)

---

*Fast UDFs: cache once, allocate wisely, avoid network per-row.*
