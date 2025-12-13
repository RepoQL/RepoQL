using AwesomeAssertions;
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
}
