using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class FindCandidatesMacroTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _store;

    public FindCandidatesMacroTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new DeterministicEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        services.AddSingleton<UriRegistry>();

        _serviceProvider = services.BuildServiceProvider();
        _store = new DuckDbDataStore(serviceProvider: _serviceProvider);
    }

    public void Dispose()
    {
        _store.Dispose();
        _serviceProvider.Dispose();
    }

    [Test]
    public void FindCandidates_Respects_PerDoc_And_Global_Caps()
    {
        var docA = CreateDocument("file:///src/a.cs", "class A {}\n");
        var docB = CreateDocument("file:///src/b.cs", "class B {}\n");

        _store.WriteEmbeddings(
        [
            new DocumentEmbedding(docA.Id, docA.Id, 0, DocumentEmbedding.TypeFull, docA.Uri, DocumentEmbedding.ScopeDocument, [0.90f, 0f, 0f, 0f], "test", 4, 0, 20),
            new DocumentEmbedding(docA.Id, docA.Id, 1, DocumentEmbedding.TypeFull, docA.Uri, DocumentEmbedding.ScopeDocument, [0.80f, 0f, 0f, 0f], "test", 4, 21, 40),
            new DocumentEmbedding(docA.Id, docA.Id, 2, DocumentEmbedding.TypeFull, docA.Uri, DocumentEmbedding.ScopeDocument, [0.10f, 0f, 0f, 0f], "test", 4, 41, 60),
            new DocumentEmbedding(docB.Id, docB.Id, 0, DocumentEmbedding.TypeFull, docB.Uri, DocumentEmbedding.ScopeDocument, [0.95f, 0f, 0f, 0f], "test", 4, 0, 20),
            new DocumentEmbedding(docB.Id, docB.Id, 1, DocumentEmbedding.TypeFull, docB.Uri, DocumentEmbedding.ScopeDocument, [0.20f, 0f, 0f, 0f], "test", 4, 21, 40)
        ]);

        var scopeJson = "[\"file:///src/a.cs\",\"file:///src/b.cs\"]";

        var rows = _store.Query($"""
            SELECT uri, chunk_index, sem_score, per_doc_rank, global_rank
            FROM _find_candidates(
                'auth',
                uri_json := '{scopeJson}',
                max_chunks := 3,
                per_doc_limit := 2
            )
            ORDER BY sem_score DESC
            """);

        rows.Should().HaveCount(3);

        rows.Count(r => string.Equals(r["uri"]?.ToString(), docA.Uri, StringComparison.OrdinalIgnoreCase))
            .Should().BeLessThanOrEqualTo(2);

        rows.Count(r => string.Equals(r["uri"]?.ToString(), docB.Uri, StringComparison.OrdinalIgnoreCase))
            .Should().BeLessThanOrEqualTo(2);

        rows.Any(r =>
                string.Equals(r["uri"]?.ToString(), docB.Uri, StringComparison.OrdinalIgnoreCase) &&
                Convert.ToInt32(r["chunk_index"]) == 0)
            .Should().BeTrue();

        var scores = rows.Select(r => Convert.ToDouble(r["sem_score"])).ToArray();
        scores.Should().BeInDescendingOrder();
    }

    [Test]
    public void FindCandidates_Uses_Structure_When_Full_Is_Missing()
    {
        var doc = CreateDocument("file:///src/structure-only.cs", "class S {}\n");

        _store.WriteEmbeddings(
        [
            new DocumentEmbedding(
                doc.Id,
                doc.Id,
                0,
                DocumentEmbedding.TypeStructure,
                doc.Uri,
                DocumentEmbedding.ScopeDocument,
                [0.88f, 0f, 0f, 0f],
                "test",
                4)
        ]);

        var rows = _store.Query("""
            SELECT uri, embedding_type, sem_score
            FROM _find_candidates(
                'auth',
                uri_json := '["file:///src/structure-only.cs"]',
                max_chunks := 5,
                per_doc_limit := 2
            )
            """);

        rows.Should().HaveCount(1);
        rows[0]["uri"]!.ToString().Should().Be(doc.Uri);
        rows[0]["embedding_type"]!.ToString().Should().Be("structure");
        Convert.ToDouble(rows[0]["sem_score"]).Should().BeGreaterThan(0.80);
    }

    private DocumentInfo CreateDocument(string uri, string content)
    {
        var docId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var artifact = new Artifact
        {
            Id = artifactId,
            Digest = $"sha256:{Guid.NewGuid():N}",
            Size = content.Length,
            Text = content,
            Headline = Path.GetFileName(uri),
            Summary = "test",
            Structure = "test",
            MediaType = SemanticMediaType.Parse("text/x-csharp")
        };

        var node = new Node
        {
            Id = docId,
            Kind = "document",
            Uri = RepoUri.Parse(uri),
            ArtifactId = artifactId
        };

        _store.IndexArtifact(new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = node,
            Children = [],
            Spans = [],
            Edges = []
        });

        return new DocumentInfo(docId, uri);
    }

    private sealed record DocumentInfo(Guid Id, string Uri);

    private sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => "deterministic";
        public int Dimension => 4;

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>([1f, 0f, 0f, 0f]);

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)[1f, 0f, 0f, 0f]).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(
            IReadOnlyList<string>? texts,
            BatchEmbeddingProgress progress,
            CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }
}
