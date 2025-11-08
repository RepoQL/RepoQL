using System.Text.Json.Nodes;
using AwesomeAssertions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Tests.TestHelpers;
using static RepoQL.Indexing.Tests.Indexing.PostProcessing.PostProcessingTestHelpers;
using RepoQL.Metrics;
using MetricsModel = RepoQL.Metrics.IndexingMetrics;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

public class StorageBackedArtifactPrunerTests
{
    [Test]
    [DisplayName("No stale documents when catalog matches pending set")]
    public async Task Given_AllDocumentsReindexed_When_PruneAsync_Then_ReturnsEmpty()
    {
        using var connection = CreateInMemoryConnection();
        SeedDocument(connection, "file:///repo/doc-a.md");

        var pruner = new StorageBackedArtifactPruner(new SingleConnectionFactory(connection), NullLogger<StorageBackedArtifactPruner>.Instance);
        var pending = new[] { CreateIndexItem("file:///repo/doc-a.md") };

        var result = await pruner.PruneAsync(pending, CancellationToken.None);
        result.DeletedArtifacts.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Returns URIs missing from the latest epoch")]
    public async Task Given_StaleDocument_When_PruneAsync_Then_ReturnsUri()
    {
        using var connection = CreateInMemoryConnection();
        SeedDocument(connection, "file:///repo/doc-live.md");
        SeedDocument(connection, "file:///repo/doc-stale.md");

        var pruner = new StorageBackedArtifactPruner(new SingleConnectionFactory(connection), NullLogger<StorageBackedArtifactPruner>.Instance);
        var pending = new[] { CreateIndexItem("file:///repo/doc-live.md") };

        var result = await pruner.PruneAsync(pending, CancellationToken.None);
        result.DeletedArtifacts.Should().ContainSingle().Which.AbsoluteUri.Should().Be("file:///repo/doc-stale.md");
    }
}

public class VectorIndexCoordinatorTests
{
    [Test]
    [DisplayName("Vector refresh only runs once per epoch until invalidated")]
    public async Task Given_VectorCoordinator_When_ApplyAsyncTwice_Then_RefreshesOnce()
    {
        var refresher = new FakeRefresher();
        var coordinator = new VectorIndexCoordinator(refresher, NullLogger<VectorIndexCoordinator>.Instance);
        var item = CreateIndexItem("file:///repo/vector.md");
        SetEpoch(item, 0);

        await coordinator.ApplyAsync(item, CancellationToken.None);
        await coordinator.ApplyAsync(item, CancellationToken.None);
        refresher.Invocations.Should().Be(1);

        await coordinator.ApplyDeletesAsync(new[] { RepoUri.Parse("file:///repo/vector.md") }, CancellationToken.None);
        await coordinator.ApplyAsync(item, CancellationToken.None);
        refresher.Invocations.Should().Be(2);
    }
}

public class DuckDbVectorIndexRefresherTests
{
    [Test]
    [DisplayName("Refresh computes embeddings for stored documents")]
    public async Task Given_Document_When_RefreshAsync_Then_EmbeddingProviderInvoked()
    {
        using var connection = CreateInMemoryConnection();
        SeedDocument(connection, "file:///repo/vector-doc.md");

        var provider = new RecordingEmbeddingProvider();
        var refresher = new DuckDbVectorIndexRefresher(new SingleConnectionFactory(connection), provider, NullLogger<DuckDbVectorIndexRefresher>.Instance);

        await refresher.RefreshAsync(CancellationToken.None);

        provider.EmbedCount.Should().Be(1);
    }
}

#region Helpers

internal sealed class SingleConnectionFactory(DuckDBConnection connection) : IDuckDBConnectionFactory
{
    private bool _provided;

    public DuckDBConnection CreateConnection()
    {
        if (_provided)
            throw new InvalidOperationException("Connection already provided.");
        _provided = true;
        return connection;
    }
}

internal sealed class FakeRefresher : IVectorIndexRefresher
{
    public int Invocations { get; private set; }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Invocations++;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingEmbeddingProvider : IEmbeddingProvider
{
    public int EmbedCount { get; private set; }
    public string Model => "test";
    public int Dimension => 4;
    public bool Enabled => true;

    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        EmbedCount++;
        return Task.FromResult<float[]?>(new[] { 0.1f, 0.2f, 0.3f, 0.4f });
    }
}

internal static class PostProcessingTestHelpers
{
    public static DuckDBConnection CreateInMemoryConnection()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        RepositoryUserDefinedFunctions.RegisterAll(connection, new MetricsModel());
        using var store = new DuckDbGraphStore(
            connection,
            metrics: new MetricsModel(),
            enableExtensions: false,
            registerUdfs: false,
            logger: NullLogger<DuckDbGraphStore>.Instance);
        store.EnsureSchema();
        return connection;
    }

    public static void SeedDocument(DuckDBConnection connection, string uri)
    {
        using var store = new DuckDbGraphStore(
            connection,
            metrics: new MetricsModel(),
            enableExtensions: false,
            registerUdfs: false,
            logger: NullLogger<DuckDbGraphStore>.Instance);
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = 4,
            MediaType = SemanticMediaType.Parse("text/plain"),
            Text = "seed"
        };
        store.UpsertArtifact(artifact);

        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse(uri),
            ArtifactId = artifact.Id,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var saved = store.UpsertDocumentByUri(docNode.Uri!, docNode);
        store.ReplaceDocumentContent(saved.Id, Array.Empty<Node>(), Array.Empty<Span>(), Array.Empty<Edge>());
    }

    public static IndexItem CreateIndexItem(string uri)
    {
        return new TestItemBuilder()
            .WithUri(uri)
            .WithContent("text")
            .Build();
    }

    public static void SetEpoch(IndexItem item, long epoch)
    {
        var method = typeof(IndexItem).GetMethod("SetEpoch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method!.Invoke(item, [epoch]);
    }
}

#endregion
