using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Inference;
using RepoQL.Contracts.Models;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

/// <summary>
/// Tests for _explore_candidates SQL macro — the unified ranking pipeline
/// where documents and objects compete in a single pool with dampened
/// semantic inheritance.
/// </summary>
public sealed class ExploreCandidatesMacroTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _store;

    public ExploreCandidatesMacroTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new DeterministicEmbeddingProvider());
        services.AddSingleton<IInferenceProvider>(new DisabledInferenceProvider());
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
    public void ExploreCandidates_ReturnsDocumentsAndObjects_InOnePool()
    {
        var doc = CreateDocumentWithChild(
            "file:///src/auth.cs",
            "namespace Auth;\nclass TokenValidator\n{\n    void ValidateToken() { }\n}\n",
            "Auth.TokenValidator.ValidateToken",
            startLine: 4,
            endLine: 4);

        var rows = _store.Query("""
            SELECT uri, node_scope, kind, score
            FROM _explore_candidates(
                'ValidateToken',
                k := 20,
                uri_glob := 'file:///src/auth.cs'
            )
            ORDER BY score DESC
            """);

        // Both document and object should be in results
        rows.Should().Contain(r => r["node_scope"]!.ToString() == "document");
        rows.Should().Contain(r => r["node_scope"]!.ToString() == "object");
    }

    [Test]
    public void ExploreCandidates_DampensSemanticInheritance_ForObjectsWithoutChunkOverlap()
    {
        var content = "namespace Demo;\nclass Foo\n{\n    void Bar() { }\n}\n";
        var doc = CreateDocumentWithChild(
            "file:///src/foo.cs",
            content,
            "Demo.Foo.Bar",
            startLine: 4,
            endLine: 4);

        // Write document-level embedding (structure type, scope=document)
        // The object's span (bytes 35-55 roughly) will NOT overlap the chunk (bytes 0-20)
        _store.WriteEmbeddings(
        [
            new DocumentEmbedding(
                doc.DocumentId, doc.DocumentId, 0,
                DocumentEmbedding.TypeStructure, doc.DocumentUri,
                DocumentEmbedding.ScopeDocument,
                [0.90f, 0f, 0f, 0f], "test", 4),
            new DocumentEmbedding(
                doc.DocumentId, doc.DocumentId, 0,
                DocumentEmbedding.TypeFull, doc.DocumentUri,
                DocumentEmbedding.ScopeDocument,
                [0.85f, 0f, 0f, 0f], "test", 4, 0, 20)
        ]);

        var rows = _store.Read("""
            SELECT node_scope, CAST(sem_score AS DOUBLE) AS sem_score, sem_provenance
            FROM _explore_candidates(
                'demo',
                k := 20,
                uri_glob := 'file:///src/foo.cs'
            )
            ORDER BY score DESC
            """,
            r => new
            {
                Scope = r.GetString(0),
                SemScore = r.GetDouble(1),
                Provenance = r.GetString(2)
            });

        var docResult = rows.First(r => r.Scope == "document");
        var objResult = rows.First(r => r.Scope == "object");

        // Document gets full semantic score (direct evidence)
        docResult.Provenance.Should().Be("direct");

        // Object without chunk overlap gets dampened score (inherited)
        objResult.Provenance.Should().Be("inherited");
        objResult.SemScore.Should().BeLessThan(docResult.SemScore,
            "objects without chunk overlap should get dampened (0.5x) semantic inheritance");
    }

    [Test]
    public void ExploreCandidates_FullSemantic_ForObjectsWithChunkOverlap()
    {
        // Content where the method starts at byte ~35
        var content = "namespace Demo;\nclass Foo\n{\n    void Bar() { }\n}\n";
        var doc = CreateDocumentWithChild(
            "file:///src/overlap.cs",
            content,
            "Demo.Foo.Bar",
            startLine: 4,
            endLine: 4,
            startByte: 28,
            endByte: 48);

        // Chunk byte range [25, 50) overlaps the object's span [28, 48)
        _store.WriteEmbeddings(
        [
            new DocumentEmbedding(
                doc.DocumentId, doc.DocumentId, 0,
                DocumentEmbedding.TypeStructure, doc.DocumentUri,
                DocumentEmbedding.ScopeDocument,
                [0.90f, 0f, 0f, 0f], "test", 4),
            new DocumentEmbedding(
                doc.DocumentId, doc.DocumentId, 0,
                DocumentEmbedding.TypeFull, doc.DocumentUri,
                DocumentEmbedding.ScopeDocument,
                [0.85f, 0f, 0f, 0f], "test", 4, 25, 50)
        ]);

        var rows = _store.Read("""
            SELECT node_scope, CAST(sem_score AS DOUBLE) AS sem_score, sem_provenance
            FROM _explore_candidates(
                'demo',
                k := 20,
                uri_glob := 'file:///src/overlap.cs'
            )
            ORDER BY score DESC
            """,
            r => new
            {
                Scope = r.GetString(0),
                SemScore = r.GetDouble(1),
                Provenance = r.GetString(2)
            });

        var docResult = rows.First(r => r.Scope == "document");
        var objResult = rows.First(r => r.Scope == "object");

        // Object overlapping the chunk gets full semantic score
        objResult.Provenance.Should().Be("chunk_overlap");
        objResult.SemScore.Should().BeApproximately(docResult.SemScore, 0.001,
            "objects overlapping the best chunk should get full semantic score");
    }

    [Test]
    public void ExploreCandidates_DocumentPromotedByBestChild()
    {
        // Document with weak headline but strong child match
        var doc = CreateDocumentWithChild(
            "file:///src/utils.cs",
            "namespace Utils;\nclass Helper\n{\n    void ProcessAuthToken() { }\n}\n",
            "Utils.Helper.ProcessAuthToken",
            startLine: 4,
            endLine: 4);

        var rows = _store.Read("""
            SELECT node_scope, score, symbol
            FROM _explore_candidates(
                'ProcessAuthToken',
                k := 20,
                uri_glob := 'file:///src/utils.cs'
            )
            ORDER BY score DESC
            """,
            r => new
            {
                Scope = r.GetString(0),
                Score = r.GetDouble(1),
                Symbol = r.IsDBNull(2) ? null : r.GetString(2)
            });

        rows.Should().HaveCountGreaterThanOrEqualTo(2);

        var docResult = rows.First(r => r.Scope == "document");
        var objResult = rows.First(r => r.Scope == "object");

        // Document score should be at least 90% of best child score
        // (promoted from best_child_score * 0.9)
        docResult.Score.Should().BeGreaterThanOrEqualTo(objResult.Score * 0.9 - 0.001,
            "document should be promoted by best child score (* 0.9)");
    }

    [Test]
    public void ExploreCandidates_ChunkEvidencePassthrough()
    {
        var doc = CreateDocument("file:///src/chunked.cs", "class Chunked { void Foo() { } }\n");

        _store.WriteEmbeddings(
        [
            new DocumentEmbedding(
                doc.Id, doc.Id, 0,
                DocumentEmbedding.TypeStructure, doc.Uri,
                DocumentEmbedding.ScopeDocument,
                [0.80f, 0f, 0f, 0f], "test", 4),
            new DocumentEmbedding(
                doc.Id, doc.Id, 0,
                DocumentEmbedding.TypeFull, doc.Uri,
                DocumentEmbedding.ScopeDocument,
                [0.75f, 0f, 0f, 0f], "test", 4, 10, 30)
        ]);

        var rows = _store.Read("""
            SELECT best_chunk_start, best_chunk_end, CAST(chunk_score AS DOUBLE) AS chunk_score
            FROM _explore_candidates(
                'chunked',
                k := 10,
                uri_glob := 'file:///src/chunked.cs'
            )
            WHERE node_scope = 'document'
            """,
            r => new
            {
                ChunkStart = r.IsDBNull(0) ? (long?)null : r.GetInt64(0),
                ChunkEnd = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                ChunkScore = r.GetDouble(2)
            });

        rows.Should().ContainSingle();
        rows[0].ChunkStart.Should().Be(10);
        rows[0].ChunkEnd.Should().Be(30);
        rows[0].ChunkScore.Should().BeGreaterThan(0, "chunk cosine score should flow through");
    }

    [Test]
    public void ExploreCandidates_ScopeFiltering_RespectsUriGlob()
    {
        CreateDocument("file:///src/target.cs", "class Target { }\n");
        CreateDocument("file:///src/other.cs", "class Other { }\n");
        CreateDocument("file:///tests/test.cs", "class Test { }\n");

        var rows = _store.Query("""
            SELECT uri
            FROM _explore_candidates(
                'class',
                k := 20,
                uri_glob := 'file:///src/**'
            )
            WHERE node_scope = 'document'
            ORDER BY uri
            """);

        rows.Should().HaveCount(2);
        rows.All(r => r["uri"]?.ToString()?.Contains("/src/") == true).Should().BeTrue();
    }

    [Test]
    public void ExploreCandidates_NoKeywords_ReturnsByRecency()
    {
        CreateDocument("file:///src/recent.cs", "class Recent { }\n");
        CreateDocument("file:///src/older.cs", "class Older { }\n");

        var rows = _store.Query("""
            SELECT uri, node_scope
            FROM _explore_candidates(
                '',
                k := 10,
                uri_glob := 'file:///src/**'
            )
            ORDER BY score DESC
            """);

        rows.Should().NotBeEmpty("empty query should return recency-based fallback");
    }

    // ========================================================================
    // Test helpers
    // ========================================================================

    private DocumentInfo CreateDocument(string uri, string content)
    {
        var docId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        _store.IndexArtifact(new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = content.Length,
                Text = content,
                Headline = Path.GetFileName(uri),
                Summary = "test",
                Structure = "test",
                MediaType = SemanticMediaType.Parse("text/x-csharp")
            },
            DocumentNode = new Node
            {
                Id = docId,
                Kind = "document",
                Uri = RepoUri.Parse(uri),
                ArtifactId = artifactId
            },
            Children = [],
            Spans = [],
            Edges = []
        });

        return new DocumentInfo(docId, uri);
    }

    private DocumentWithChildInfo CreateDocumentWithChild(
        string documentUri,
        string content,
        string symbol,
        int startLine,
        int endLine,
        long? startByte = null,
        long? endByte = null)
    {
        var parsedDocumentUri = RepoUri.Parse(documentUri);
        var documentId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var spanId = Guid.NewGuid();
        var symbolUri = RepoUri.FromSymbol(parsedDocumentUri.Container, symbol, startLine, endLine);

        _store.IndexArtifact(new ParsedArtifact
        {
            Artifact = new Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = content.Length,
                Text = content,
                Headline = Path.GetFileName(documentUri),
                Summary = "test",
                Structure = symbol,
                MediaType = SemanticMediaType.Parse("text/x-csharp")
            },
            DocumentNode = new Node
            {
                Id = documentId,
                Kind = "document",
                Uri = parsedDocumentUri,
                ArtifactId = artifactId,
                Props = new JsonObject()
            },
            Children =
            [
                new Node
                {
                    Id = childId,
                    Kind = "csharp.member",
                    Uri = symbolUri,
                    SpanId = spanId,
                    Headline = symbol.Split('.').Last(),
                    Structure = $"void {symbol.Split('.').Last()}()",
                    Props = new JsonObject
                    {
                        ["name"] = symbol.Split('.').Last(),
                        ["symbol"] = symbol
                    }
                }
            ],
            Spans =
            [
                new Span
                {
                    Id = spanId,
                    DocumentId = documentId,
                    StartLine = startLine,
                    EndLine = endLine,
                    StartByte = startByte,
                    EndByte = endByte,
                    StartColumn = 1,
                    EndColumn = 15
                }
            ],
            Edges =
            [
                new Edge
                {
                    SrcId = documentId,
                    DstId = childId,
                    Type = "HAS_PART",
                    IsComposition = true,
                    Ordinal = 0
                }
            ]
        });

        return new DocumentWithChildInfo(documentId, childId, documentUri, symbolUri.ToString()!);
    }

    private sealed record DocumentInfo(Guid Id, string Uri);

    private sealed record DocumentWithChildInfo(Guid DocumentId, Guid ChildId, string DocumentUri, string ChildUri);

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
