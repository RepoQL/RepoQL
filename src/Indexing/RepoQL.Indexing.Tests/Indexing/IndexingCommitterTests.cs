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
using TUnit.Core;

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

        var writer = A.Fake<IDatabaseWriter>();
        WriteOperation? capturedOperation = null;
        A.CallTo(() => writer.EnqueueAndWaitAsync(A<WriteOperation>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                capturedOperation = call.GetArgument<WriteOperation>(0);
                return new ValueTask<CommitResult>(new CommitResult { Success = true });
            });

        var committer = new IndexingCommitter(writer, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        capturedOperation.Should().NotBeNull();
        capturedOperation!.Type.Should().Be(WriteOperationType.ReplaceDocument);
        capturedOperation.Uri.Should().Be(item.Uri);
        capturedOperation.ParsedData.Should().NotBeNull();
        capturedOperation.ParsedData!.Annotations.Length.Should().Be(1);

        // Simulate the writer completing successfully.
        await capturedOperation.OnCommitted!(capturedOperation, new CommitResult { Success = true });

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

        var writer = A.Fake<IDatabaseWriter>();
        var catalog = A.Fake<IDocumentCatalog>();
        var committer = new IndexingCommitter(writer, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => writer.EnqueueAndWaitAsync(A<WriteOperation>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Propagates database failures so the caller can retry")]
    public async Task Given_DatabaseWriterFails_When_CommitAsync_Then_Throws()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/doc.md");

        var writer = A.Fake<IDatabaseWriter>();
        A.CallTo(() => writer.EnqueueAndWaitAsync(A<WriteOperation>._, A<CancellationToken>._))
            .Returns(new ValueTask<CommitResult>(new CommitResult
            {
                Success = false,
                Error = new InvalidOperationException("DuckDB failure")
            }));

        var catalog = A.Fake<IDocumentCatalog>();
        var committer = new IndexingCommitter(writer, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        var act = async () => await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Database commit failed for *");
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
    }

    private static async Task<IndexItem> CreatePopulatedItemAsync(string uri)
    {
        var item = IndexingTestItemBuilder.ForMarkdown().WithUri(uri).WithContent("# Title").Build();
        item.MediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc");
        item.DigestHex = Convert.ToHexString(await item.RawArtifact.Digest.WithCancellation(CancellationToken.None));

        var documentNode = new Node
        {
            Kind = "document",
            Uri = item.Uri,
            ArtifactId = Guid.NewGuid()
        };

        item.Records = new Records
        {
            Artifacts = [],
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
