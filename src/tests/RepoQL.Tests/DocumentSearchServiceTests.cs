using System.Reflection;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Search;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Explore.Search;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

internal sealed class DocumentSearchServiceTests
{
    [Test]
    public async Task SearchAsync_WithFileScope_ReturnsOnlyFilesUnderScope()
    {
        using var context = new DocumentSearchTestContext();
        var now = DateTimeOffset.UtcNow;

        context.SeedDocument("file:///src/RepoQL.ConsoleApp/Program.cs", "text/plain;kind=code.csharp", now.AddMinutes(-2), "Program");
        context.SeedDocument("file:///src/RepoQL.Explore/ExploreOrchestrator.cs", "text/plain;kind=code.csharp", now.AddMinutes(-1), "Explore");
        context.SeedDocument("file:///docs/README.md", "text/markdown;kind=markdown.doc", now, "Docs");
        context.SeedDocument("help:///quickstart.md", "text/markdown;kind=markdown.doc", now, "Help");

        var service = new DocumentSearchService(context.Store);
        var result = await service.SearchAsync("file:///src/**", question: null, limit: 20, CancellationToken.None);

        result.Documents.Should().HaveCount(2);
        result.Documents.Should().OnlyContain(d =>
            d.Uri.StartsWith("file:///src/", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task SearchAsync_WithLineFragmentScope_ReturnsContainingDocument()
    {
        using var context = new DocumentSearchTestContext();
        var now = DateTimeOffset.UtcNow;

        context.SeedDocument("file:///src/RepoQL.ConsoleApp/Program.cs", "text/plain;kind=code.csharp", now.AddMinutes(-2), "Program");
        context.SeedDocument("file:///src/RepoQL.ConsoleApp/Other.cs", "text/plain;kind=code.csharp", now.AddMinutes(-1), "Other");

        var service = new DocumentSearchService(context.Store);
        var result = await service.SearchAsync(
            "file:///src/RepoQL.ConsoleApp/Program.cs#line=1,1",
            question: null,
            limit: 20,
            CancellationToken.None);

        result.Documents.Should().ContainSingle();
        result.Documents[0].Uri.Should().Be("file:///src/RepoQL.ConsoleApp/Program.cs");
    }

    [Test]
    public async Task SearchAsync_ScopeInventory_PrioritizesCodeOverVendorAndDocs()
    {
        using var context = new DocumentSearchTestContext();
        var now = DateTimeOffset.UtcNow;

        // Vendor and docs are newer, but code should still rank first for inventory.
        context.SeedDocument(
            "file:///src/RepoQL.Web/wwwroot/lib/bootstrap/bootstrap.bundle.min.js.map",
            "application/json;kind=json.doc",
            now,
            "Bootstrap map");

        context.SeedDocument(
            "file:///src/RepoQL.Documentation/repoql/tools/query/functions/tree.md",
            "text/markdown;kind=markdown.doc",
            now.AddMinutes(-1),
            "Tree docs");

        context.SeedDocument(
            "file:///src/RepoQL.ConsoleApp/Tools/ExploreTool.cs",
            "text/plain;kind=code.csharp",
            now.AddMinutes(-2),
            "Explore tool");

        var service = new DocumentSearchService(context.Store);
        var result = await service.SearchAsync("file:///src/**", question: null, limit: 10, CancellationToken.None);

        result.Documents.Should().NotBeEmpty();
        result.Documents[0].Uri.Should().Be("file:///src/RepoQL.ConsoleApp/Tools/ExploreTool.cs");
    }

    [Test]
    public async Task SearchAsync_WithFileScopeAndQuestion_ReturnsOnlyFilesUnderScope()
    {
        using var context = new DocumentSearchTestContext();
        var now = DateTimeOffset.UtcNow;

        context.SeedDocument("file:///src/RepoQL.ConsoleApp/Program.cs", "text/plain;kind=code.csharp", now.AddMinutes(-2), "Program entry point");
        context.SeedDocument("file:///src/RepoQL.ConsoleApp/Tools/ExploreTool.cs", "text/plain;kind=code.csharp", now.AddMinutes(-1), "Explore tool");
        context.SeedDocument("file:///docs/program-overview.md", "text/markdown;kind=markdown.doc", now, "Program overview");

        var service = new DocumentSearchService(context.Store);
        var result = await service.SearchAsync("file:///src/**", question: "program", limit: 20, CancellationToken.None);

        result.Documents.Should().NotBeEmpty();
        result.Documents.Should().OnlyContain(d =>
            d.Uri.StartsWith("file:///src/", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void GetChunkScores_WithMixedValidAndInvalidDocIds_ReturnsChunksForValidIdsOnly()
    {
        using var context = new DocumentSearchTestContext();
        var service = new DocumentSearchService(context.Store);

        var validDocId = Guid.NewGuid();
        var validNodeId = Guid.NewGuid();
        const string uri = "file:///src/RepoQL.ConsoleApp/Search/DocumentSearchService.cs";

        context.SeedDocumentEmbedding(
            docId: validDocId,
            nodeId: validNodeId,
            uri: uri,
            scope: "document",
            startByte: 12,
            endByte: 34);
        context.SeedSpan(
            documentId: validDocId,
            startByte: 12,
            endByte: 34,
            startLine: 7,
            endLine: 9);

        var chunkScores = InvokeGetChunkScores(service, [validDocId.ToString("D"), "not-a-guid"]);

        chunkScores.Should().ContainKey(uri);
        chunkScores[uri].Should().ContainSingle();
        chunkScores[uri][0].StartLine.Should().Be(7);
        chunkScores[uri][0].EndLine.Should().Be(9);
    }

    [Test]
    public void GetChunkScores_WithOnlyInvalidDocIds_ReturnsEmpty()
    {
        using var context = new DocumentSearchTestContext();
        var service = new DocumentSearchService(context.Store);

        var chunkScores = InvokeGetChunkScores(service, ["not-a-guid", "still-not-a-guid"]);

        chunkScores.Should().BeEmpty();
    }

    private static Dictionary<string, IReadOnlyList<ChunkScore>> InvokeGetChunkScores(
        DocumentSearchService service,
        IReadOnlyList<string> docIds)
    {
        var method = typeof(DocumentSearchService)
            .GetMethod("GetChunkScores", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate DocumentSearchService.GetChunkScores");

        var result = method.Invoke(service, [docIds, CancellationToken.None]);
        return result as Dictionary<string, IReadOnlyList<ChunkScore>>
            ?? throw new InvalidOperationException("GetChunkScores returned an unexpected result");
    }

    private sealed class DocumentSearchTestContext : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public DocumentSearchTestContext()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new RepositoryConfiguration { Path = "/repo" });
            services.AddSingleton<UriRegistry>();
            services.AddSingleton<IEmbeddingProvider>(new DisabledEmbeddingProvider());
            services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
            services.AddSingleton<IMcpToolCaller?>(_ => null);

            _serviceProvider = services.BuildServiceProvider();
            Registry = _serviceProvider.GetRequiredService<UriRegistry>();
            Store = new DuckDbDataStore(":memory:", serviceProvider: _serviceProvider);
        }

        public UriRegistry Registry { get; }
        public DuckDbDataStore Store { get; }

        public void SeedDocument(string uri, string mediaType, DateTimeOffset updatedAt, string headline)
        {
            var documentUri = RepoUri.Parse(uri);
            var artifact = new ArtifactModel
            {
                Id = Guid.NewGuid(),
                Digest = Guid.NewGuid().ToString("N"),
                Size = headline.Length,
                MediaType = SemanticMediaType.Parse(mediaType),
                Text = headline,
                Headline = headline,
                Summary = headline,
                Structure = headline
            };

            var documentNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "document",
                Uri = documentUri,
                ArtifactId = artifact.Id,
                Props = new JsonObject(),
                Headline = headline,
                Structure = headline,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            };

            Store.IndexArtifact(new ParsedArtifact
            {
                Artifact = artifact,
                DocumentNode = documentNode
            });

            Registry.SetIndexed(documentUri, lineCount: 1, new Dictionary<RepoUri, SymbolEntry>());
        }

        public void SeedDocumentEmbedding(Guid docId, Guid nodeId, string uri, string scope, long startByte, long endByte)
        {
            Store.ExecuteRaw($"""
                INSERT INTO document_embedding
                    (doc_id, node_id, chunk_index, embedding_type, uri, scope, model, dim, embedding, start_byte, end_byte, updated_at)
                VALUES
                    ('{docId:D}'::UUID, '{nodeId:D}'::UUID, 0, 'structure', '{EscapeSql(uri)}', '{EscapeSql(scope)}', 'test-model', 3, [0.1,0.2,0.3]::FLOAT[], {startByte}, {endByte}, NOW())
                """);
        }

        public void SeedSpan(Guid documentId, long startByte, long endByte, int startLine, int endLine)
        {
            Store.ExecuteRaw($"""
                INSERT INTO span (id, document_id, start_byte, end_byte, start_line, start_column, end_line, end_column)
                VALUES ('{Guid.NewGuid():D}'::UUID, '{documentId:D}'::UUID, {startByte}, {endByte}, {startLine}, 1, {endLine}, 1)
                """);
        }

        public void Dispose()
        {
            Store.Dispose();
            _serviceProvider.Dispose();
        }

        private sealed class DisabledEmbeddingProvider : IEmbeddingProvider
        {
            public bool Enabled => false;
            public string Model => "disabled";
            public int Dimension => 384;

            public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default)
                => Task.FromResult<float[]?>(null);

            public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default)
                => Task.FromResult<float[]?>(null);

            public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

            public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

            public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        }

        private sealed class DisabledLlmProvider : ILlmProvider
        {
            public bool Enabled => false;
            public string Model => "disabled";

            public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
                => Task.FromResult("LLM disabled in tests");

            public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
                => Task.FromResult(new LlmSummaryResult("LLM disabled in tests"));

            public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
                => Task.FromResult("LLM disabled in tests");

            public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
        }

        private static string EscapeSql(string value) => value.Replace("'", "''");
    }
}
