using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Inference;

namespace RepoQL.Data.DuckDB.Tests;

public class UdfFrameworkTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _db;

    public UdfFrameworkTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new TestEmbeddingProvider());
        services.AddSingleton<IInferenceProvider>(new TestInferenceProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        services.AddSingleton<UriRegistry>();
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
        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private sealed class TestInferenceProvider : IInferenceProvider
    {
        public bool Available => false;

        public Task<InferenceResult> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
            => Task.FromResult(new InferenceResult { Content = "Inference disabled" });

        public Task<InferenceResult> CompleteWithToolsAsync(
            InferenceRequest request,
            ToolOptions toolOptions,
            Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
            CancellationToken ct = default)
            => CompleteAsync(request, ct);
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
