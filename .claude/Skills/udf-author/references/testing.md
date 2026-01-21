# Testing UDFs

How to test UDFs effectively in RepoQL.

---

## Testing Levels

| Level | What It Tests | Speed | Confidence |
|-------|---------------|-------|------------|
| Unit test (C#) | Logic in isolation | Fast | Medium |
| Integration test | UDF + DuckDB registration | Medium | High |
| SQL test | Full round-trip including macro | Slow | Highest |

---

## Unit Testing the Logic

Test the C# method directly without DuckDB:

```csharp
[Test]
public void FormatBytes_ShouldFormatMegabytes()
{
    var udf = new FormatUdf();

    var result = udf.FormatBytes("1048576");  // 1 MB

    result.Should().Be("1.0 MB");
}

[Test]
public void FormatBytes_ShouldHandleNull()
{
    var udf = new FormatUdf();

    var result = udf.FormatBytes(null);

    result.Should().Be("0 B");
}

[Test]
public void FormatBytes_ShouldHandleInvalidInput()
{
    var udf = new FormatUdf();

    var result = udf.FormatBytes("not a number");

    result.Should().Be("0 B");
}
```

### Testing with Dependencies

Use FakeItEasy to mock injected services:

```csharp
[Test]
public async Task EmbedText_ShouldReturnVector_WhenProviderEnabled()
{
    var embeddings = A.Fake<IEmbeddingProvider>();
    A.CallTo(() => embeddings.Enabled).Returns(true);
    A.CallTo(() => embeddings.EmbedAsync(A<string>._, A<CancellationToken>._))
        .Returns(new float[] { 0.1f, 0.2f, 0.3f });

    var udf = new EmbedUdf(embeddings);

    var result = udf.EmbedText("hello");

    result.Should().Be("[0.1,0.2,0.3]");
}

[Test]
public void EmbedText_ShouldReturnNull_WhenProviderDisabled()
{
    var embeddings = A.Fake<IEmbeddingProvider>();
    A.CallTo(() => embeddings.Enabled).Returns(false);

    var udf = new EmbedUdf(embeddings);

    var result = udf.EmbedText("hello");

    result.Should().BeNull();
}
```

---

## Integration Testing with DuckDB

Test UDF registration and invocation:

```csharp
[Test]
public async Task FormatBytes_ShouldBeCallableFromSql()
{
    await using var dataStore = await CreateTestDataStore();

    var result = await dataStore.QueryScalarAsync<string>(
        "SELECT format_bytes('1048576')");

    result.Should().Be("1.0 MB");
}

[Test]
public async Task Search_ShouldReturnRows()
{
    await using var dataStore = await CreateTestDataStore();
    // Seed test data...

    var results = await dataStore.QueryAsync<SearchResult>(
        "SELECT * FROM search('test query', k := 5)");

    results.Should().HaveCountLessOrEqualTo(5);
    results.Should().AllSatisfy(r => r.Uri.Should().NotBeNullOrEmpty());
}
```

### Test Data Store Setup

Use `TestServiceCollectionExtensions` for consistent setup:

```csharp
private async Task<DuckDbDataStore> CreateTestDataStore()
{
    var services = new ServiceCollection();
    services.AddTestDuckDbDataStore();  // In-memory, test configuration

    var provider = services.BuildServiceProvider();
    var dataStore = provider.GetRequiredService<DuckDbDataStore>();

    await dataStore.EnsureSchemaAsync();
    return dataStore;
}
```

---

## Testing Structured UDFs

Verify column structure and types:

```csharp
[Test]
public async Task ParseUri_ShouldReturnCorrectColumns()
{
    await using var dataStore = await CreateTestDataStore();

    var result = await dataStore.QuerySingleAsync<dynamic>(
        "SELECT * FROM parse_uri('https://example.com/path')");

    // Verify column presence
    ((string)result.scheme).Should().Be("https");
    ((string)result.host).Should().Be("example.com");
    ((string)result.path).Should().Be("/path");
}

[Test]
public async Task SplitPath_ShouldReturnMultipleRows()
{
    await using var dataStore = await CreateTestDataStore();

    var results = await dataStore.QueryAsync<PathSegment>(
        "SELECT * FROM split_path('/src/components/Button.tsx')");

    results.Should().HaveCount(3);
    results[0].Segment.Should().Be("src");
    results[1].Segment.Should().Be("components");
    results[2].Segment.Should().Be("Button.tsx");
}
```

---

## Testing Default Parameters

Verify macro defaults work correctly:

```csharp
[Test]
public async Task Search_ShouldUseDefaultK()
{
    await using var dataStore = await CreateTestDataStore();

    // Without k parameter - should use default (10)
    var results = await dataStore.QueryAsync<SearchResult>(
        "SELECT * FROM search('query')");

    results.Should().HaveCountLessOrEqualTo(10);
}

[Test]
public async Task Search_ShouldAcceptNamedParameters()
{
    await using var dataStore = await CreateTestDataStore();

    // With named parameter
    var results = await dataStore.QueryAsync<SearchResult>(
        "SELECT * FROM search('query', k := 3)");

    results.Should().HaveCountLessOrEqualTo(3);
}
```

---

## Testing Error Handling

Verify graceful degradation:

```csharp
[Test]
public async Task Udf_ShouldHandleNullInput()
{
    await using var dataStore = await CreateTestDataStore();

    var result = await dataStore.QueryScalarAsync<string>(
        "SELECT format_bytes(NULL)");

    result.Should().Be("0 B");  // Or whatever your null handling returns
}

[Test]
public async Task Udf_ShouldNotThrowOnInvalidInput()
{
    await using var dataStore = await CreateTestDataStore();

    // Should not throw, should return error or null
    var act = async () => await dataStore.QueryScalarAsync<string>(
        "SELECT parse_json('not valid json')");

    await act.Should().NotThrowAsync();
}
```

---

## Test Organization

```
RepoQL.Data.DuckDB.Tests/
├── UdfTests/
│   ├── FormatUdfTests.cs      # Unit tests for FormatUdf
│   ├── SearchUdfTests.cs      # Unit tests for SearchUdf
│   └── ...
├── Integration/
│   ├── UdfRegistrationTests.cs    # All UDFs register correctly
│   ├── UdfSqlInvocationTests.cs   # UDFs callable from SQL
│   └── MacroGenerationTests.cs    # Macros generated correctly
```

---

## Test Checklist

Before shipping a new UDF:

- [ ] Unit tests for core logic
- [ ] Null input handling tested
- [ ] Invalid input handling tested
- [ ] Integration test: UDF callable from SQL
- [ ] Default parameters work (if applicable)
- [ ] Named parameters work (if macro generated)
- [ ] Structured UDF returns expected columns
- [ ] Error cases return gracefully (no exceptions to SQL)

---

*Test the logic. Test the integration. Test the edge cases.*
