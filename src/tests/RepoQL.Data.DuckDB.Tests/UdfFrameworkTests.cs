using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB.Tests;

public class UdfFrameworkTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _db;

    public UdfFrameworkTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new TestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new TestLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        _serviceProvider = services.BuildServiceProvider();
        _db = new DuckDbDataStore(serviceProvider: _serviceProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
    }

    private class TestEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => "test-model";
        public int Dimension => 384;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
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

    [Test]
    [DisplayName("embed_status macro returns provider info")]
    public void EmbedStatus_ReturnsProviderInfo()
    {
        var results = _db.Read(
            "SELECT embed_status() as status",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().NotBeNull();
        results[0].Should().Contain("test-model");
    }

    [Test]
    [DisplayName("indexing_diagnostics macro returns diagnostics text")]
    public void IndexingDiagnostics_ReturnsDiagnosticsText()
    {
        var results = _db.Read(
            "SELECT indexing_diagnostics() as diag",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().NotBeNullOrEmpty();
    }

    [Test]
    [DisplayName("indexing_queue macro returns JSON array")]
    public void IndexingQueue_ReturnsJsonArray()
    {
        var results = _db.Read(
            "SELECT indexing_queue() as queue",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().StartWith("[");
        results[0].Should().EndWith("]");
    }
}
