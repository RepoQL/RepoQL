using AwesomeAssertions;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Testing.Indexing;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal class DuckDbVectorIndexRefresherTests
{
    [Test]
    [DisplayName("Refresh computes embeddings for stored documents")]
    public async Task Given_Document_When_RefreshAsync_Then_EmbeddingProviderInvoked()
    {
        var provider = new RecordingEmbeddingProvider();
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);

        // Seed a document
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = 100,
            MediaType = SemanticMediaType.Parse("text/plain"),
            Text = "test content for embedding"
        };

        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse("file:///repo/vector-doc.md"),
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var parsedArtifact = new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = documentNode,
            Children = Array.Empty<Node>(),
            Spans = Array.Empty<Span>(),
            Edges = Array.Empty<Edge>()
        };

        database.IndexArtifact(documentNode.Uri, parsedArtifact);

        var refresher = new DuckDbVectorIndexRefresher(database, provider, NullLogger<DuckDbVectorIndexRefresher>.Instance);

        await refresher.RefreshAsync(CancellationToken.None);

        provider.EmbedCount.Should().Be(1);
    }

    [Test]
    [DisplayName("Refresh chunks medium documents and stores byte offsets")]
    public async Task Given_MediumDocument_When_RefreshAsync_Then_WritesChunkEmbeddingsWithByteOffsets()
    {
        var provider = new RecordingEmbeddingProvider();
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);

        // Build a medium doc (>2000 chars) with a multi-byte char to validate UTF-8 byte offsets.
        var prefixLen = 1400;
        var text = new string('a', prefixLen) + "é" + new string('b', 3000 - prefixLen - 1);

        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = 100,
            MediaType = SemanticMediaType.Parse("text/plain"),
            Headline = "headline",
            Summary = "summary",
            Structure = "structure",
            Text = text
        };

        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse("file:///repo/chunked.txt"),
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var parsedArtifact = new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = documentNode,
            Children = Array.Empty<Node>(),
            Spans = Array.Empty<Span>(),
            Edges = Array.Empty<Edge>()
        };

        database.IndexArtifact(documentNode.Uri, parsedArtifact);

        var refresher = new DuckDbVectorIndexRefresher(database, provider, NullLogger<DuckDbVectorIndexRefresher>.Instance);
        await refresher.RefreshAsync(CancellationToken.None);

        // Chunking uses chunkSize=1500, overlap=150 => stride=1350 => 3 chunks for 3000 chars.
        provider.EmbedCount.Should().Be(3);

        var rows = database.Read(
            $"""
            SELECT chunk_index, start_byte, end_byte
            FROM document_embedding
            WHERE doc_id = '{documentNode.Id:D}'::UUID
              AND scope = 'document'
              AND embedding_type = 'full'
            ORDER BY chunk_index;
            """,
            r => new
            {
                Chunk = r.GetInt32(0),
                Start = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                End = r.IsDBNull(2) ? (long?)null : r.GetInt64(2)
            });

        rows.Should().HaveCount(3);

        static long BytesAt(string s, int charPos) => Encoding.UTF8.GetByteCount(s.AsSpan(0, charPos));

        rows[0].Chunk.Should().Be(0);
        rows[0].Start.Should().Be(0);
        rows[0].End.Should().Be(BytesAt(text, 1500));

        rows[1].Chunk.Should().Be(1);
        rows[1].Start.Should().Be(BytesAt(text, 1350));
        rows[1].End.Should().Be(BytesAt(text, 2850));

        rows[2].Chunk.Should().Be(2);
        rows[2].Start.Should().Be(BytesAt(text, 2700));
        rows[2].End.Should().Be(BytesAt(text, 3000));
    }

    [Test]
    [DisplayName("Refresh embeds large documents using structure only")]
    public async Task Given_LargeDocument_When_RefreshAsync_Then_EmbedsStructureOnly()
    {
        var provider = new RecordingEmbeddingProvider();
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);

        var largeText = new string('x', 150 * 1024 + 10);
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = largeText.Length,
            MediaType = SemanticMediaType.Parse("text/plain"),
            Headline = "big headline",
            Structure = "outline",
            Text = largeText
        };

        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse("file:///repo/large.txt"),
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var parsedArtifact = new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = documentNode,
            Children = Array.Empty<Node>(),
            Spans = Array.Empty<Span>(),
            Edges = Array.Empty<Edge>()
        };

        database.IndexArtifact(documentNode.Uri, parsedArtifact);

        var refresher = new DuckDbVectorIndexRefresher(database, provider, NullLogger<DuckDbVectorIndexRefresher>.Instance);
        await refresher.RefreshAsync(CancellationToken.None);

        provider.EmbedCount.Should().Be(1);
        provider.EmbeddedTextLengths.Should().HaveCount(1);
        provider.EmbeddedTextLengths[0].Should().BeLessThan(5000); // should not embed full text_content

        var rows = database.Read(
            $"""
            SELECT chunk_index, start_byte, end_byte
            FROM document_embedding
            WHERE doc_id = '{documentNode.Id:D}'::UUID
              AND scope = 'document'
              AND embedding_type = 'full';
            """,
            r => new
            {
                Chunk = r.GetInt32(0),
                Start = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                End = r.IsDBNull(2) ? (long?)null : r.GetInt64(2)
            });

        rows.Should().HaveCount(1);
        rows[0].Chunk.Should().Be(0);
        rows[0].Start.Should().BeNull();
        rows[0].End.Should().BeNull();
    }
}
