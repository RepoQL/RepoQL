using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing;

public class IndexingCommitterTests
{
    [Test]
    [DisplayName("Commits records and updates catalog after successful database write")]
    public async Task Given_ValidItem_When_CommitAsync_Then_WritesAndUpdatesCatalog()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/doc.md");

        var catalog = A.Fake<IDocumentCatalog>();
        DocumentCatalogEntry? appliedEntry = null;
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._))
            .Invokes(call => appliedEntry = call.GetArgument<DocumentCatalogEntry>(0));

        using var db = new DuckDbDataStore(); // in-memory
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert - verify the document was written to the database
        var doc = db.GetDocumentByUri(item.Uri);
        doc.Should().NotBeNull("document should be written to database");

        appliedEntry.Should().NotBeNull();
        appliedEntry!.Uri.Should().Be(item.Uri);
        appliedEntry.Digest.Should().Be(item.DigestHex);
        appliedEntry.MediaType.Should().Be(item.MediaType);
        appliedEntry.PhysicalPath.Should().Be(item.RawArtifact.PhysicalPath);
    }

    [Test]
    [DisplayName("Skips database write when records are missing")]
    public async Task Given_ItemWithoutRecords_When_CommitAsync_Then_DoesNothing()
    {
        // Arrange
        var item = IndexingTestItemBuilder.ForMarkdown().WithContent("Hello").Build();
        item.DigestHex = Convert.ToHexString(await item.RawArtifact.Digest.WithCancellation(CancellationToken.None));
        item.MediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc");

        using var db = new DuckDbDataStore(); // in-memory
        var catalog = A.Fake<IDocumentCatalog>();
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert - no document should exist (no records to commit)
        var allNodes = db.GetAllNodes();
        allNodes.Should().BeEmpty("no records means no write");
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Propagates database failures so the caller can retry")]
    public async Task Given_DatabaseWriterFails_When_CommitAsync_Then_Throws()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/doc.md");

        // Create a disposed store to force an error
        var db = new DuckDbDataStore();
        db.Dispose();

        var catalog = A.Fake<IDocumentCatalog>();
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        var act = async () => await committer.CommitAsync(item, CancellationToken.None);

        // Assert - should throw due to disposed connection
        await act.Should().ThrowAsync<Exception>();
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
    }

    private static async Task<IndexItem> CreatePopulatedItemAsync(string uri)
    {
        var item = IndexingTestItemBuilder.ForMarkdown().WithUri(uri).WithContent("# Title").Build();
        item.MediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc");
        item.DigestHex = Convert.ToHexString(await item.RawArtifact.Digest.WithCancellation(CancellationToken.None));

        var artifactId = Guid.NewGuid();
        var artifact = new RepoQL.Contracts.Models.Artifact
        {
            Id = artifactId,
            Digest = "abc123",
            Size = 7,
            MediaType = SemanticMediaType.Parse("text/markdown"),
            Headline = "Title"
        };

        var documentNode = new Node
        {
            Kind = "document",
            Uri = item.Uri,
            ArtifactId = artifactId
        };

        item.Records = new Records
        {
            Artifacts = [artifact],
            Nodes = [documentNode],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        item.AnnotationsList.Add(new Annotation
        {
            Kind = "lint",
            Severity = "warning",
            Source = "test",
            Message = "Example warning",
            ScopeDocumentId = documentNode.Id
        });

        return item;
    }
}
