using AwesomeAssertions;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Testing.Indexing;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal class EmbeddingCoordinatorTests
{
    [Test]
    [DisplayName("Embedding refresh only runs once per epoch until invalidated")]
    public async Task Given_EmbeddingCoordinator_When_ApplyAsyncTwice_Then_RefreshesOnce()
    {
        var refresher = new FakeRefresher();
        var coordinator = new EmbeddingCoordinator(refresher, logger: NullLogger<EmbeddingCoordinator>.Instance);
        var item = BuildItem("file:///repo/embedding.md", includeDocNode: true, includeArtifact: true);
        item.SetEpoch(0);

        await coordinator.ApplyAsync([item], CancellationToken.None);
        await coordinator.ApplyAsync([item], CancellationToken.None);
        refresher.TargetedInvocations.Should().Be(1);
        refresher.LastDocumentIds.Should().ContainSingle();

        await coordinator.ApplyDeletesAsync(new[] { RepoUri.Parse("file:///repo/vector.md") }, CancellationToken.None);
        await coordinator.ApplyAsync([item], CancellationToken.None);
        refresher.Invocations.Should().Be(1);
        refresher.TargetedInvocations.Should().Be(1);
    }

    [Test]
    [DisplayName("Embedding refresh targets all dirty documents in the idle batch")]
    public async Task Given_BatchOfItems_When_ApplyAsync_Then_TargetsAllDocumentIds()
    {
        var refresher = new FakeRefresher();
        var coordinator = new EmbeddingCoordinator(refresher, logger: NullLogger<EmbeddingCoordinator>.Instance);
        var first = BuildItem("file:///repo/first.md", includeDocNode: true, includeArtifact: true);
        var second = BuildItem("file:///repo/second.md", includeDocNode: true, includeArtifact: true);
        first.SetEpoch(3);
        second.SetEpoch(3);

        await coordinator.ApplyAsync([first, second], CancellationToken.None);

        var firstId = first.Records!.Nodes[0].Id;
        var secondId = second.Records!.Nodes[0].Id;
        refresher.TargetedInvocations.Should().Be(1);
        refresher.LastDocumentIds.Should().HaveCount(2);
        refresher.LastDocumentIds.Should().Contain(firstId);
        refresher.LastDocumentIds.Should().Contain(secondId);
    }

    [Test]
    [DisplayName("Structure embeddings only run for items with document nodes and artifacts")]
    public async Task Given_ItemsWithoutStructure_When_GenerateStructureEmbeddingsAsync_Then_WritesExpectedEmbeddings()
    {
        var provider = new RecordingEmbeddingProvider();
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);
        var coordinator = new EmbeddingCoordinator(
            new FakeRefresher(),
            database,
            provider,
            EmbeddingMode.StructureOnly,
            NullLogger<EmbeddingCoordinator>.Instance);

        var items = new[]
        {
            BuildItem("file:///repo/with-structure.md", includeDocNode: true, includeArtifact: true),
            BuildItem("file:///repo/no-artifact.md", includeDocNode: true, includeArtifact: false),
            BuildItem("file:///repo/no-doc.md", includeDocNode: false, includeArtifact: true)
        };

        await coordinator.GenerateStructureEmbeddingsAsync(items, CancellationToken.None);

        provider.EmbedCount.Should().Be(1, "only items with document nodes and artifacts are eligible for structure embeddings");
        var counts = database.Read(
            "SELECT COUNT(*) FROM document_embedding WHERE embedding_type = 'structure'",
            reader => reader.GetInt64(0));
        counts.Should().ContainSingle();
        counts[0].Should().Be(1);
    }

    [Test]
    [DisplayName("Skips structure embedding for files already marked as embedded")]
    public async Task Given_AlreadyEmbeddedItem_When_GenerateStructureEmbeddingsAsync_Then_SkipsThatItem()
    {
        var provider = new RecordingEmbeddingProvider();
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);
        var registry = new UriRegistry();

        var alreadyEmbeddedUri = RepoUri.Parse("file:///repo/already-embedded.md");
        registry.TryRegisterDiscovered(alreadyEmbeddedUri);
        registry.SetEmbedded(alreadyEmbeddedUri, 1);

        var coordinator = new EmbeddingCoordinator(
            new FakeRefresher(),
            database,
            provider,
            EmbeddingMode.StructureOnly,
            NullLogger<EmbeddingCoordinator>.Instance,
            registry);

        var items = new[]
        {
            BuildItem("file:///repo/already-embedded.md", includeDocNode: true, includeArtifact: true),
            BuildItem("file:///repo/new-item.md", includeDocNode: true, includeArtifact: true)
        };

        await coordinator.GenerateStructureEmbeddingsAsync(items, CancellationToken.None);

        provider.EmbedCount.Should().Be(1, "already embedded files should be skipped during idle catch-up");
    }

    [Test]
    [DisplayName("Startup content embedding catch-up triggers a full refresh for indexed repositories")]
    public async Task Given_IndexedDocumentsWithoutContentEmbeddings_When_CoordinatorStarts_Then_StartupCatchUpRefreshesContent()
    {
        using var database = new DuckDbDataStore(path: null, logger: NullLogger<DuckDbDataStore>.Instance);
        SeedIndexedDocument(database, "file:///repo/startup-catchup.md");

        var refresher = new FakeRefresher();
        using var coordinator = new EmbeddingCoordinator(
            refresher,
            db: database,
            logger: NullLogger<EmbeddingCoordinator>.Instance);

        await WaitForAsync(() => refresher.Invocations >= 1);
        refresher.TargetedInvocations.Should().Be(0);
    }

    [Test]
    [DisplayName("Startup content embedding catch-up refreshes when only incompatible full embeddings exist")]
    public async Task Given_IndexedDocumentsWithOnlyIncompatibleContentEmbeddings_When_CoordinatorStarts_Then_StartupCatchUpRefreshesContent()
    {
        var provider = new FixedModelEmbeddingProvider("current-model", 4);
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);
        var document = SeedIndexedDocument(database, "file:///repo/startup-catchup-incompatible.md");

        database.WriteEmbeddings([
            new DocumentEmbedding(
                document.Id,
                document.Id,
                0,
                DocumentEmbedding.TypeFull,
                document.Uri.AbsoluteUri,
                DocumentEmbedding.ScopeDocument,
                [1f, 2f, 3f, 4f],
                "old-model",
                4)
        ]);

        var refresher = new FakeRefresher();
        using var coordinator = new EmbeddingCoordinator(
            refresher,
            db: database,
            embeddingProvider: provider,
            logger: NullLogger<EmbeddingCoordinator>.Instance);

        await WaitForAsync(() => refresher.Invocations >= 1);
        refresher.TargetedInvocations.Should().Be(0);
    }

    [Test]
    [DisplayName("Explicit active-model recheck refreshes incompatible content embeddings without restart")]
    public async Task Given_IndexedDocumentsWithOnlyIncompatibleContentEmbeddings_When_RecheckingActiveModel_Then_RefreshesContent()
    {
        var provider = new FixedModelEmbeddingProvider("current-model", 4);
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);
        var document = SeedIndexedDocument(database, "file:///repo/recheck-incompatible.md");
        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(document.Uri);
        registry.SetIndexed(document.Uri, lineCount: 1, new Dictionary<RepoUri, SymbolEntry>());

        database.WriteEmbeddings([
            new DocumentEmbedding(
                document.Id,
                document.Id,
                0,
                DocumentEmbedding.TypeFull,
                document.Uri.AbsoluteUri,
                DocumentEmbedding.ScopeDocument,
                [1f, 2f, 3f, 4f],
                "old-model",
                4)
        ]);

        var refresher = new FakeRefresher();
        using var coordinator = new EmbeddingCoordinator(
            refresher,
            db: database,
            embeddingProvider: provider,
            logger: NullLogger<EmbeddingCoordinator>.Instance,
            uriRegistry: registry,
            enableStartupCatchUp: false);

        var refreshTriggered = await coordinator.RecheckActiveEmbeddingModelAsync(CancellationToken.None);

        refreshTriggered.Should().BeTrue();
        refresher.Invocations.Should().Be(1);
        registry[document.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Pending);
    }

    [Test]
    [DisplayName("Targeted registry sync batches doc ids with bounded query size")]
    public void Given_LargeTargetedDocSet_When_Batching_Then_BatchesAreBoundedAndDeduplicated()
    {
        var uniqueIds = Enumerable.Range(0, EmbeddingCoordinator.RegistrySyncBatchSize * 2 + 5)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var inputIds = new List<Guid>(uniqueIds)
        {
            uniqueIds[0],
            uniqueIds[3],
            uniqueIds[^1]
        };

        var batches = EmbeddingCoordinator.BatchDocumentIds(inputIds);
        var flattened = batches.SelectMany(batch => batch).ToArray();

        batches.Should().HaveCount(3);
        batches.All(batch => batch.Length <= EmbeddingCoordinator.RegistrySyncBatchSize).Should().BeTrue();
        flattened.Should().HaveCount(uniqueIds.Length);
        flattened.Distinct().Should().HaveCount(uniqueIds.Length);
        foreach (var id in uniqueIds)
        {
            flattened.Should().Contain(id);
        }
    }

    private static IndexItem BuildItem(string uri, bool includeDocNode, bool includeArtifact)
    {
        var item = IndexingTestItemBuilder.ForMarkdown("sample.md").WithUri(uri).WithContent("text").Build();
        var artifactId = Guid.NewGuid();

        var artifacts = includeArtifact
            ? new[]
            {
                new ArtifactModel
                {
                    Id = artifactId,
                    Digest = Guid.NewGuid().ToString("N"),
                    Size = 4,
                    MediaType = SemanticMediaType.Parse("text/markdown"),
                    Headline = "Title",
                    Structure = "- Section"
                }
            }
            : Array.Empty<ArtifactModel>();

        var nodes = includeDocNode
            ? new[]
            {
                new Node
                {
                    Id = Guid.NewGuid(),
                    Kind = "document",
                    Uri = item.Uri,
                    ArtifactId = includeArtifact ? artifactId : Guid.NewGuid()
                }
            }
            : Array.Empty<Node>();

        item.Records = new Records
        {
            Artifacts = artifacts,
            Nodes = nodes,
            Spans = Array.Empty<Span>(),
            Edges = Array.Empty<Edge>(),
            Annotations = Array.Empty<Annotation>(),
            AnnotationSources = Array.Empty<string>()
        };

        return item;
    }

    private static Node SeedIndexedDocument(DuckDbDataStore database, string uri)
    {
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = 4,
            MediaType = SemanticMediaType.Parse("text/markdown"),
            Headline = "Title",
            Structure = "- Section",
            Text = "text"
        };

        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse(uri),
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        database.IndexArtifact(documentNode.Uri, new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = documentNode,
            Children = Array.Empty<Node>(),
            Spans = Array.Empty<Span>(),
            Edges = Array.Empty<Edge>()
        });

        return documentNode;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromMilliseconds(timeoutMs))
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private sealed class FixedModelEmbeddingProvider(string model, int dimension) : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => model;
        public int Dimension => dimension;

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }
}
