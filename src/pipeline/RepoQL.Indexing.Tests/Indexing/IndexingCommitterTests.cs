using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
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
        item.DigestHex = await item.RawArtifact.Digest.WithCancellation(CancellationToken.None);
        item.MediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc");

        using var db = new DuckDbDataStore(); // in-memory
        var catalog = A.Fake<IDocumentCatalog>();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(item.Uri);
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance, registry);

        // Act
        var result = await committer.CommitAsync(item, CancellationToken.None);

        // Assert - no document should exist (no records to commit)
        var allNodes = db.GetAllNodes();
        result.Should().Be(RepoQL.Indexing.Indexing.Commit.CommitOutcome.Skipped);
        allNodes.Should().BeEmpty("no records means no write");
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
        registry.Should().ContainKey(item.Uri);
        registry[item.Uri].Status.Should().Be(UriStatus.Failed);
        registry[item.Uri].Error.Should().Contain("commit: no records were produced");
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
        var act = async () => { await committer.CommitAsync(item, CancellationToken.None); };

        // Assert - should throw due to disposed connection
        await act.Should().ThrowAsync<Exception>();
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Queued commit faults waiting task when batch flush fails")]
    public async Task Given_FlushFails_When_CommitAsync_Then_WaiterIsFaulted()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/queued-flush-failure.md");
        var db = new DuckDbDataStore();
        db.Dispose();

        var catalog = A.Fake<IDocumentCatalog>();
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        var commitTask = committer.CommitAsync(item, CancellationToken.None);
        committer.FlushPendingItems();

        // Assert
        Func<Task> act = async () => await commitTask;
        await act.Should().ThrowAsync<Exception>();
    }

    [Test]
    [DisplayName("Completes other queued commits when one catalog upsert fails after artifact write")]
    public async Task Given_CatalogUpsertFailsForOneQueuedItem_When_FlushPendingItems_Then_OtherWaitersStillComplete()
    {
        var successful = await CreatePopulatedItemAsync("file:///repo/catalog-success.md");
        var failed = await CreatePopulatedItemAsync("file:///repo/catalog-failed.md");

        var catalog = A.Fake<IDocumentCatalog>();
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>.That.Matches(entry => entry.Uri == failed.Uri)))
            .Throws(new InvalidOperationException("catalog boom"));

        using var db = new DuckDbDataStore();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(successful.Uri);
        registry.TryRegisterDiscovered(failed.Uri);
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance, registry);

        var successfulTask = committer.CommitAsync(successful, CancellationToken.None);
        var failedTask = committer.CommitAsync(failed, CancellationToken.None);

        committer.FlushPendingItems();

        (await successfulTask).Should().Be(CommitOutcome.Committed);

        Func<Task> act = async () => await failedTask;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*catalog boom*");

        db.GetDocumentByUri(successful.Uri).Should().NotBeNull();
        db.GetDocumentByUri(failed.Uri).Should().NotBeNull();
        registry[failed.Uri].Status.Should().Be(UriStatus.Failed);
        registry[failed.Uri].Error.Should().Contain("catalog update failed");
    }

    [Test]
    [DisplayName("Writes structure embeddings in the same commit flush and marks URI embedded")]
    public async Task Given_ItemWithStructureEmbedding_When_CommitAsync_Then_WritesEmbeddingAndUpdatesRegistry()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/embedded.md");
        var documentNode = item.Records!.Nodes.First(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        item.StructureEmbedding = new DocumentEmbedding(
            documentNode.Id,
            documentNode.Id,
            ChunkIndex: 0,
            DocumentEmbedding.TypeStructure,
            item.Uri.ToString(),
            DocumentEmbedding.ScopeDocument,
            new[] { 0.01f, 0.02f, 0.03f, 0.04f },
            "test-model",
            4);

        using var db = new DuckDbDataStore(); // in-memory
        var catalog = A.Fake<IDocumentCatalog>();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(item.Uri);
        using var committer = new IndexingCommitter(
            db,
            catalog,
            NullLogger<IndexingCommitter>.Instance,
            registry,
            new EnabledEmbeddingProvider(),
            EmbeddingMode.Full);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        var counts = db.Read(
            "SELECT COUNT(*) FROM document_embedding WHERE embedding_type = 'structure' AND uri = 'file:///repo/embedded.md'",
            reader => reader.GetInt64(0));
        counts.Should().ContainSingle();
        counts[0].Should().Be(1);

        registry.Should().ContainKey(item.Uri);
        registry[item.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Embedded);
        registry[item.Uri].EmbeddedChunkCount.Should().Be(1);
    }

    [Test]
    [DisplayName("Continues commit and updates catalog when structure embedding write fails in queued commit path")]
    public async Task Given_InvalidStructureEmbedding_When_CommitAsync_Then_DocumentAndCatalogStillCommit()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/embedding-write-fail-queued.md");
        item.StructureEmbedding = CreateInvalidStructureEmbedding(item);

        using var db = new DuckDbDataStore(); // in-memory
        var catalog = A.Fake<IDocumentCatalog>();
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance);

        // Act
        var act = async () => { await committer.CommitAsync(item, CancellationToken.None); };

        // Assert
        await act.Should().NotThrowAsync();

        var doc = db.GetDocumentByUri(item.Uri);
        doc.Should().NotBeNull("artifact commit should succeed even if structure embedding write fails");
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    [DisplayName("Marks embedding failed and continues catalog updates when structure embedding write fails in explicit batch path")]
    public async Task Given_InvalidStructureEmbedding_When_CommitBatchAsync_Then_MarksEmbeddingFailedAndCommitsCatalog()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/embedding-write-fail-batch.md");
        item.StructureEmbedding = CreateInvalidStructureEmbedding(item);

        using var db = new DuckDbDataStore(); // in-memory
        var catalog = A.Fake<IDocumentCatalog>();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(item.Uri);
        using var committer = new IndexingCommitter(
            db,
            catalog,
            NullLogger<IndexingCommitter>.Instance,
            registry,
            new EnabledEmbeddingProvider(),
            EmbeddingMode.Full);

        // Act
        var act = async () => await committer.CommitBatchAsync([item], CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        var doc = db.GetDocumentByUri(item.Uri);
        doc.Should().NotBeNull("artifact commit should succeed even if structure embedding write fails");
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>._)).MustHaveHappenedOnceExactly();
        registry[item.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Failed);
        registry[item.Uri].Error.Should().Contain("structure embedding write failed");
    }

    [Test]
    [DisplayName("Does not write structure embeddings for items whose artifact commit failed")]
    public async Task Given_PartialBatchFailure_When_CommitBatchAsync_Then_OnlySuccessfulEmbeddingsAreWritten()
    {
        // Arrange
        var successful = await CreatePopulatedItemAsync("file:///repo/embedding-success.md");
        var failed = await CreatePopulatedItemAsync("file:///repo/embedding-failed.md");
        ForceDocumentNodeIdConflict(successful, failed);
        successful.StructureEmbedding = CreateStructureEmbedding(successful, new[] { 0.1f, 0.2f, 0.3f, 0.4f });
        failed.StructureEmbedding = CreateStructureEmbedding(failed, new[] { 0.5f, 0.6f, 0.7f, 0.8f });

        using var db = new DuckDbDataStore();
        var catalog = A.Fake<IDocumentCatalog>();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(successful.Uri);
        registry.TryRegisterDiscovered(failed.Uri);
        using var committer = new IndexingCommitter(
            db,
            catalog,
            NullLogger<IndexingCommitter>.Instance,
            registry,
            new EnabledEmbeddingProvider(),
            EmbeddingMode.Full);

        // Act
        await committer.CommitBatchAsync([successful, failed], CancellationToken.None);

        // Assert
        var counts = db.Read(
            "SELECT uri, COUNT(*) FROM document_embedding WHERE embedding_type = 'structure' GROUP BY uri ORDER BY uri",
            reader => (reader.GetString(0), reader.GetInt64(1)));

        counts.Should().ContainSingle();
        counts[0].Item1.Should().Be(successful.Uri.ToString());
        counts[0].Item2.Should().Be(1);
        registry[successful.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Embedded);
        registry[failed.Uri].EmbeddingStatus.Should().NotBe(EmbeddingStatus.Embedded);
    }

    [Test]
    [DisplayName("Continues explicit batch when one catalog upsert fails after artifact write")]
    public async Task Given_CatalogUpsertFailsForOneExplicitBatchItem_When_CommitBatchAsync_Then_OtherItemsStillUpdate()
    {
        var successful = await CreatePopulatedItemAsync("file:///repo/batch-catalog-success.md");
        var failed = await CreatePopulatedItemAsync("file:///repo/batch-catalog-failed.md");

        var catalog = A.Fake<IDocumentCatalog>();
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>.That.Matches(entry => entry.Uri == failed.Uri)))
            .Throws(new InvalidOperationException("catalog boom"));

        using var db = new DuckDbDataStore();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(successful.Uri);
        registry.TryRegisterDiscovered(failed.Uri);
        using var committer = new IndexingCommitter(db, catalog, NullLogger<IndexingCommitter>.Instance, registry);

        await committer.CommitBatchAsync([successful, failed], CancellationToken.None);

        db.GetDocumentByUri(successful.Uri).Should().NotBeNull();
        db.GetDocumentByUri(failed.Uri).Should().NotBeNull();
        registry[failed.Uri].Status.Should().Be(UriStatus.Failed);
        registry[failed.Uri].Error.Should().Contain("catalog update failed");
        A.CallTo(() => catalog.ApplyUpsert(A<DocumentCatalogEntry>.That.Matches(entry => entry.Uri == successful.Uri)))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    [DisplayName("Marks URI embedding as not applicable when structure embeddings are disabled")]
    public async Task Given_EmbeddingModeNone_When_CommitAsync_Then_MarksEmbeddingNotApplicable()
    {
        // Arrange
        var item = await CreatePopulatedItemAsync("file:///repo/not-applicable.md");

        using var db = new DuckDbDataStore(); // in-memory
        var catalog = A.Fake<IDocumentCatalog>();
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(item.Uri);
        using var committer = new IndexingCommitter(
            db,
            catalog,
            NullLogger<IndexingCommitter>.Instance,
            registry,
            embeddingProvider: null,
            embeddingMode: EmbeddingMode.None);

        // Act
        await committer.CommitAsync(item, CancellationToken.None);

        // Assert
        registry.Should().ContainKey(item.Uri);
        registry[item.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.NotApplicable);

        var counts = db.Read(
            "SELECT COUNT(*) FROM document_embedding WHERE embedding_type = 'structure' AND uri = 'file:///repo/not-applicable.md'",
            reader => reader.GetInt64(0));
        counts.Should().ContainSingle();
        counts[0].Should().Be(0);
    }

    private static async Task<IndexItem> CreatePopulatedItemAsync(string uri)
    {
        var item = IndexingTestItemBuilder.ForMarkdown().WithUri(uri).WithContent("# Title").Build();
        item.MediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc");
        item.DigestHex = await item.RawArtifact.Digest.WithCancellation(CancellationToken.None);

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

    private static DocumentEmbedding CreateInvalidStructureEmbedding(IndexItem item)
    {
        var documentNode = item.Records!.Nodes.First(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        return new DocumentEmbedding(
            documentNode.Id,
            documentNode.Id,
            ChunkIndex: 0,
            DocumentEmbedding.TypeStructure,
            item.Uri.ToString(),
            DocumentEmbedding.ScopeDocument,
            null!,
            "test-model",
            4);
    }

    private static DocumentEmbedding CreateStructureEmbedding(IndexItem item, float[] vector)
    {
        var documentNode = item.Records!.Nodes.First(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        return new DocumentEmbedding(
            documentNode.Id,
            documentNode.Id,
            ChunkIndex: 0,
            DocumentEmbedding.TypeStructure,
            item.Uri.ToString(),
            DocumentEmbedding.ScopeDocument,
            vector,
            "test-model",
            4);
    }

    private static void ForceDocumentNodeIdConflict(IndexItem source, IndexItem target)
    {
        var conflictingId = source.Records!.Nodes.First(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase)).Id;
        var targetDocumentNode = target.Records!.Nodes.First(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        target.Records = new Records
        {
            Artifacts = target.Records.Artifacts,
            Nodes = [targetDocumentNode with { Id = conflictingId }],
            Spans = target.Records.Spans,
            Edges = target.Records.Edges,
            Annotations = target.Records.Annotations,
            AnnotationSources = target.Records.AnnotationSources
        };
    }
    private sealed class EnabledEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "test-model";
        public int Dimension => 4;
        public bool Enabled => true;

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float[]?>());

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float[]?>());

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float[]?>());
    }
}

