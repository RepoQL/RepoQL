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

public sealed class FindCandidatesMacroTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _store;

    public FindCandidatesMacroTests()
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

    [Test]
    public void SearchCandidates_UriGlob_SupportsSemicolonScope()
    {
        var docA = CreateDocument("file:///src/a.cs", "class A {}\n");
        var docB = CreateDocument("file:///src/b.cs", "class B {}\n");
        _ = CreateDocument("file:///src/c.cs", "class C {}\n");

        var probe = _store.Query("""
            SELECT
                matches_glob('file:///src/a.cs', 'file:///src/a.cs;file:///src/b.cs') AS match_a,
                matches_glob('file:///src/c.cs', 'file:///src/a.cs;file:///src/b.cs') AS match_c
            """);
        probe.Should().HaveCount(1);
        probe[0]["match_a"]?.ToString().Should().Be("true");
        probe[0]["match_c"]?.ToString().Should().Be("false");

        var matchRows = _store.Query("""
            SELECT uri
            FROM repo_index
            WHERE matches_glob(uri, 'file:///src/a.cs;file:///src/b.cs') IS TRUE
            ORDER BY uri
            """);
        matchRows.Should().HaveCount(2);

        var lexicalRows = _store.Query("""
            SELECT node_id
            FROM _search_lexical(
                'missing term',
                uri_glob := 'file:///src/a.cs;file:///src/b.cs'
            )
            """);
        lexicalRows.Should().HaveCount(2);

        var rows = _store.Query("""
            SELECT DISTINCT uri
            FROM _search_candidates(
                'missing term',
                k := 50,
                uri_glob := 'file:///src/a.cs;file:///src/b.cs'
            )
            WHERE node_scope = 'document'
            ORDER BY uri
            """);

        rows.Should().HaveCount(2);
        rows.Select(r => r["uri"]?.ToString()).Should().BeEquivalentTo([docA.Uri, docB.Uri]);
    }

    [Test]
    public void SearchLexical_BodyMatch_FindsDocumentWithoutExtraDocJoinFilter()
    {
        var doc = CreateDocument(
            "file:///src/body-match.cs",
            "namespace Demo;\nclass BodyMatch { const string Marker = \"specialwidgetbody\"; }\n");

        var rows = _store.Query("""
            SELECT node_id, doc_id
            FROM _search_lexical(
                'specialwidgetbody',
                uri_glob := 'file:///src/body-match.cs'
            )
            """);

        rows.Should().HaveCount(1);
        Guid.Parse(rows[0]["node_id"]!.ToString()!).Should().Be(doc.Id);
        Guid.Parse(rows[0]["doc_id"]!.ToString()!).Should().Be(doc.Id);
    }

    [Test]
    public void SearchCandidates_EnrichesChildNodeMetadata_WithSimpleDocumentJoins()
    {
        var doc = CreateDocumentWithChild(
            "file:///src/widget.cs",
            """
            namespace Demo;
            class Widget
            {
                void Run() { }
            }
            """,
            "Demo.Widget.Run",
            startLine: 4,
            endLine: 4);

        var rows = _store.Read("""
            SELECT kind, uri, path, line_start, line_end
            FROM _search_candidates(
                'Demo.Widget.Run',
                k := 10,
                uri_glob := 'file:///src/widget.cs'
            )
            ORDER BY score DESC, uri
            """,
            r => new
            {
                Kind = r.GetString(0),
                Uri = r.GetString(1),
                Path = r.GetString(2),
                LineStart = r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                LineEnd = r.IsDBNull(4) ? (int?)null : r.GetInt32(4)
            });

        rows.Should().ContainSingle(r => r.Uri == doc.ChildUri);
        var child = rows.Single(r => r.Uri == doc.ChildUri);
        child.Kind.Should().Be("csharp.member");
        child.Path.Should().Be(doc.DocumentUri);
        child.LineStart.Should().Be(4);
        child.LineEnd.Should().Be(4);
    }

    [Test]
    public void Related_EnrichesChildNodeMetadata_WithSimpleDocumentJoins()
    {
        var seed = CreateDocument(
            "file:///src/seed.cs",
            "namespace Demo;\nclass Seed { }\n");

        var relatedDoc = CreateDocumentWithChild(
            "file:///src/widget-helper.cs",
            """
            namespace Demo;
            class WidgetHelper
            {
                void Run() { }
            }
            """,
            "Demo.WidgetHelper.Run",
            startLine: 4,
            endLine: 4);

        _store.WriteEmbeddings(
        [
            new DocumentEmbedding(
                seed.Id,
                seed.Id,
                0,
                DocumentEmbedding.TypeFull,
                seed.Uri,
                DocumentEmbedding.ScopeDocument,
                [1f, 0f, 0f, 0f],
                "test",
                4),
            new DocumentEmbedding(
                relatedDoc.DocumentId,
                relatedDoc.ChildId,
                0,
                DocumentEmbedding.TypeFull,
                relatedDoc.ChildUri,
                DocumentEmbedding.ScopeObject,
                [1f, 0f, 0f, 0f],
                "test",
                4)
        ]);

        var rows = _store.Read("""
            SELECT kind, uri, path, line_start, line_end
            FROM related(
                'file:///src/seed.cs',
                k := 10,
                uri_glob := 'file:///src/widget-helper.cs'
            )
            ORDER BY score DESC, uri
            """,
            r => new
            {
                Kind = r.GetString(0),
                Uri = r.GetString(1),
                Path = r.GetString(2),
                LineStart = r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                LineEnd = r.IsDBNull(4) ? (int?)null : r.GetInt32(4)
            });

        rows.Should().ContainSingle(r => r.Uri == relatedDoc.ChildUri);
        var child = rows.Single(r => r.Uri == relatedDoc.ChildUri);
        child.Kind.Should().Be("csharp.member");
        child.Path.Should().Be(relatedDoc.DocumentUri);
        child.LineStart.Should().Be(4);
        child.LineEnd.Should().Be(4);
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

    private DocumentWithChildInfo CreateDocumentWithChild(
        string documentUri,
        string content,
        string symbol,
        int startLine,
        int endLine)
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
                    Headline = "Run",
                    Props = new JsonObject
                    {
                        ["name"] = "Run",
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
