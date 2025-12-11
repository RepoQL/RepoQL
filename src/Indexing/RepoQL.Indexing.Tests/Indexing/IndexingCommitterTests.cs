using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
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

        var db = A.Fake<IRepoDatabase>();
        ParsedArtifact? capturedArtifact = null;
        A.CallTo(() => db.IndexArtifactBatch(A<IReadOnlyList<(RepoUri, ParsedArtifact)>>._))
            .ReturnsLazily(call =>
            {
                var items = call.GetArgument<IReadOnlyList<(RepoUri, ParsedArtifact)>>(0)!;
                if (items.Count > 0)
                    capturedArtifact = items[0].Item2;
                return items.Select(_ => new IndexResult(Guid.NewGuid(), false)).ToList();
            });

        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        capturedArtifact.Should().NotBeNull();
        capturedArtifact!.DocumentNode.Should().NotBeNull();
        capturedArtifact.Annotations.Count.Should().Be(1);

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

        var db = A.Fake<IRepoDatabase>();
        var catalog = A.Fake<IDocumentCatalog>();
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => db.IndexArtifactBatch(A<IReadOnlyList<(RepoUri, ParsedArtifact)>>._)).MustNotHaveHappened();
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Propagates database failures so the caller can retry")]
    public async Task Given_DatabaseWriterFails_When_CommitAsync_Then_Throws()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/doc.md");

        var db = A.Fake<IRepoDatabase>();
        A.CallTo(() => db.IndexArtifactBatch(A<IReadOnlyList<(RepoUri, ParsedArtifact)>>._))
            .Throws(new InvalidOperationException("DuckDB failure"));

        var catalog = A.Fake<IDocumentCatalog>();
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        var act = async () => await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("DuckDB failure");
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
