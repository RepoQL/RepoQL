using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Core.Operations;

namespace RepoQL.Data.DuckDB.Tests;

public class OperationsUdfTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _db;
    private readonly UriRegistry _registry;
    private readonly OperationManager _operationManager;

    public OperationsUdfTests()
    {
        _registry = new UriRegistry();
        _operationManager = new OperationManager(_registry);

        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new TestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new TestLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        services.AddSingleton(_registry);
        services.AddSingleton<IOperationManager>(_operationManager);
        _serviceProvider = services.BuildServiceProvider();
        _db = new DuckDbDataStore(serviceProvider: _serviceProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
    }

    [Test]
    [DisplayName("_operations_internal UDF returns all operations")]
    public void Operations_ReturnsAllOperations()
    {
        // Create two operations
        _operationManager.CreateOperation("test: first", Array.Empty<RepoUri>());
        _operationManager.CreateOperation("test: second", Array.Empty<RepoUri>());

        // The UDF returns JSON string (requires dummy param due to DuckDB.NET limitation)
        var jsonResults = _db.Read(
            "SELECT _operations_internal('')",
            r => r.IsDBNull(0) ? "[]" : r.GetString(0));

        jsonResults.Should().HaveCount(1);
        var json = jsonResults[0];

        json.Should().Contain("\"description\":\"test: first\"");
        json.Should().Contain("\"description\":\"test: second\"");
        json.Should().Contain("\"state\":\"Completed\"");
    }

    [Test]
    [DisplayName("_operation_internal UDF returns single operation by ID")]
    public void Operation_ReturnsSingleOperation()
    {
        var op = _operationManager.CreateOperation("test: single", Array.Empty<RepoUri>());

        var jsonResults = _db.Read(
            $"SELECT _operation_internal('{op.Id}')",
            r => r.IsDBNull(0) ? "[]" : r.GetString(0));

        jsonResults.Should().HaveCount(1);
        var json = jsonResults[0];

        json.Should().Contain($"\"id\":\"{op.Id}\"");
        json.Should().Contain("\"description\":\"test: single\"");
    }

    [Test]
    [DisplayName("_operation_internal UDF returns empty for nonexistent ID")]
    public void Operation_ReturnsEmptyForNonexistent()
    {
        var jsonResults = _db.Read(
            "SELECT _operation_internal('nonexistent')",
            r => r.IsDBNull(0) ? "[]" : r.GetString(0));

        jsonResults.Should().HaveCount(1);
        jsonResults[0].Should().Be("[]");
    }

    [Test]
    [DisplayName("_operation_log_internal UDF returns log entries")]
    public async Task OperationLog_ReturnsLogEntries()
    {
        // Use empty scope for immediate completion (simpler test)
        var op = _operationManager.CreateOperation("test: log", Array.Empty<RepoUri>());

        // Wait for completion
        await op.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The UDF returns JSON string - parse it
        var jsonResults = _db.Read(
            $"SELECT _operation_log_internal('{op.Id}')",
            r => r.IsDBNull(0) ? "[]" : r.GetString(0));

        jsonResults.Should().HaveCount(1);
        var json = jsonResults[0];

        // Should contain created and completed entries (type field uses constants)
        json.Should().Contain($"\"type\":\"{OperationEntry.TypeCreated}\"");
        json.Should().Contain($"\"type\":\"{OperationEntry.TypeCompleted}\"");
    }

    [Test]
    [DisplayName("Operations view returns rows with kind and scope")]
    public void OperationsView_ReturnsKindAndScope()
    {
        _operationManager.CreateOperation("import: github://owner/repo", Array.Empty<RepoUri>());
        _operationManager.CreateOperation("startup: /home/user/project", Array.Empty<RepoUri>());

        var results = _db.Read(
            "SELECT kind, scope, state FROM Operations ORDER BY kind",
            r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        results.Should().HaveCount(2);
        results[0].Should().Be(("import", "github://owner/repo", "Completed"));
        results[1].Should().Be(("startup", "/home/user/project", "Completed"));
    }

    [Test]
    [DisplayName("Operations view includes elapsed_s")]
    public void OperationsView_IncludesElapsed()
    {
        _operationManager.CreateOperation("test: elapsed", Array.Empty<RepoUri>());

        var results = _db.Read(
            "SELECT CAST(elapsed_s AS DOUBLE) FROM Operations",
            r => r.GetDouble(0));

        results.Should().HaveCount(1);
        results[0].Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    [DisplayName("_operations() macro returns rows")]
    public void OperationsMacro_ReturnsRows()
    {
        _operationManager.CreateOperation("test: macro", Array.Empty<RepoUri>());

        var results = _db.Read(
            "SELECT id, description, state FROM _operations()",
            r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        results.Should().HaveCount(1);
        results[0].Item2.Should().Be("test: macro");
        results[0].Item3.Should().Be("Completed");
    }

    [Test]
    [DisplayName("_operation() macro returns single row by ID")]
    public void OperationMacro_ReturnsSingleRow()
    {
        var op = _operationManager.CreateOperation("test: single macro", Array.Empty<RepoUri>());

        var results = _db.Read(
            $"SELECT description FROM _operation('{op.Id}')",
            r => r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().Be("test: single macro");
    }

    [Test]
    [DisplayName("_operation_log() macro returns log entries")]
    public async Task OperationLogMacro_ReturnsEntries()
    {
        var op = _operationManager.CreateOperation("test: log macro", Array.Empty<RepoUri>());
        await op.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var results = _db.Read(
            $"SELECT type FROM _operation_log('{op.Id}')",
            r => r.GetString(0));

        results.Should().Contain(OperationEntry.TypeCreated);
        results.Should().Contain(OperationEntry.TypeCompleted);
    }

    [Test]
    [DisplayName("_operation_internal UDF returns progress counters")]
    public async Task Operations_ReturnsProgressCounters()
    {
        // Use empty scope for immediate completion
        var op = _operationManager.CreateOperation("test: counters", Array.Empty<RepoUri>());

        await op.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The UDF returns JSON string
        var jsonResults = _db.Read(
            $"SELECT _operation_internal('{op.Id}')",
            r => r.IsDBNull(0) ? "[]" : r.GetString(0));

        jsonResults.Should().HaveCount(1);
        var json = jsonResults[0];

        // Should contain counters (all 0 for empty scope, snake_case keys)
        json.Should().Contain("\"total_files\":0");
        json.Should().Contain("\"indexed_count\":0");
        json.Should().Contain("\"embedded_count\":0");
        json.Should().Contain("\"failed_count\":0");
    }

    private class TestEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => "test-model";
        public int Dimension => 384;
        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private class TestLlmProvider : ILlmProvider
    {
        public bool Enabled => false;
        public string Model => "disabled";
        public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult("LLM disabled");
        public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult(new LlmSummaryResult("LLM disabled"));
        public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
            => Task.FromResult("LLM disabled");
        public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
