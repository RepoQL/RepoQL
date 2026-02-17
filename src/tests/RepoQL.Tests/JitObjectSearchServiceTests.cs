using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Search;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Data.DuckDB;

namespace RepoQL.Tests;

internal sealed class JitObjectSearchServiceTests
{
    [Test]
    public void LoadPersistedObjectEmbeddings_WithMixedValidAndInvalidNodeIds_ReturnsValidMatchesOnly()
    {
        using var context = new JitObjectSearchTestContext();
        var service = new JitObjectSearchService(context.Store, embeddingProvider: null);

        var docId = Guid.NewGuid();
        var validNodeId = Guid.NewGuid();
        const string model = "jit-test-model";
        const int dim = 3;

        context.SeedObjectEmbedding(
            docId: docId,
            nodeId: validNodeId,
            uri: "file:///src/RepoQL.ConsoleApp/Search/JitObjectSearchService.cs#symbol=JitObjectSearchService.SearchAsync",
            model: model,
            dim: dim,
            embeddingLiteral: "[0.11,0.22,0.33]");

        var result = service.LoadPersistedObjectEmbeddings(
            [validNodeId.ToString("D"), "not-a-guid"],
            model,
            dim);

        result.Should().ContainKey(validNodeId.ToString("D"));
        result[validNodeId.ToString("D")].Should().HaveCount(3);
    }

    [Test]
    public void LoadPersistedObjectEmbeddings_WithOnlyInvalidNodeIds_ReturnsEmpty()
    {
        using var context = new JitObjectSearchTestContext();
        var service = new JitObjectSearchService(context.Store, embeddingProvider: null);

        var result = service.LoadPersistedObjectEmbeddings(
            ["not-a-guid", "still-not-a-guid"],
            "jit-test-model",
            3);

        result.Should().BeEmpty();
    }

    private sealed class JitObjectSearchTestContext : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public JitObjectSearchTestContext()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new RepositoryConfiguration { Path = "/repo" });
            services.AddSingleton<UriRegistry>();
            services.AddSingleton<IMcpToolCaller?>(_ => null);

            _serviceProvider = services.BuildServiceProvider();
            Store = new DuckDbDataStore(":memory:", serviceProvider: _serviceProvider);
        }

        public DuckDbDataStore Store { get; }

        public void SeedObjectEmbedding(Guid docId, Guid nodeId, string uri, string model, int dim, string embeddingLiteral)
        {
            Store.ExecuteRaw($"""
                INSERT INTO document_embedding
                    (doc_id, node_id, chunk_index, embedding_type, uri, scope, model, dim, embedding, start_byte, end_byte, updated_at)
                VALUES
                    ('{docId:D}'::UUID, '{nodeId:D}'::UUID, 0, 'structure', '{EscapeSql(uri)}', 'object', '{EscapeSql(model)}', {dim}, {embeddingLiteral}::FLOAT[], 0, 12, NOW())
                """);
        }

        public void Dispose()
        {
            Store.Dispose();
            _serviceProvider.Dispose();
        }

        private static string EscapeSql(string value) => value.Replace("'", "''");
    }
}
