using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal class StorageBackedArtifactPrunerTests
{
    [Test]
    [DisplayName("No stale documents when catalog matches pending set")]
    public async Task Given_AllDocumentsReindexed_When_PruneAsync_Then_ReturnsEmpty()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///repo/doc-a.md");

        var pruner = new StorageBackedArtifactPruner(
            store.DataStore,
            () => true,
            NullLogger<StorageBackedArtifactPruner>.Instance);
        var observedUris = new[]
        {
            IndexingTestItemFactory.CreateUri("file:///repo/doc-a.md")
        };

        var result = await pruner.PruneAsync(observedUris, CancellationToken.None);
        result.DeletedArtifacts.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Returns URIs missing from the latest epoch")]
    public async Task Given_StaleDocument_When_PruneAsync_Then_ReturnsUri()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///repo/doc-live.md");
        store.SeedDocument("file:///repo/doc-stale.md");

        var pruner = new StorageBackedArtifactPruner(
            store.DataStore,
            () => true,
            NullLogger<StorageBackedArtifactPruner>.Instance);
        var observedUris = new[]
        {
            IndexingTestItemFactory.CreateUri("file:///repo/doc-live.md")
        };

        var result = await pruner.PruneAsync(observedUris, CancellationToken.None);
        result.DeletedArtifacts.Should().ContainSingle().Which.AbsoluteUri.Should().Be("file:///repo/doc-stale.md");
    }

    [Test]
    [DisplayName("Skips pruning when reindexing is not active")]
    public async Task Given_NotReindexing_When_PruneAsync_Then_ReturnsEmpty()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///repo/doc.md");

        var pruner = new StorageBackedArtifactPruner(
            store.DataStore,
            () => false,
            NullLogger<StorageBackedArtifactPruner>.Instance);

        var observedUris = Array.Empty<RepoUri>();
        var result = await pruner.PruneAsync(observedUris, CancellationToken.None);
        result.DeletedArtifacts.Should().BeEmpty();
    }
}
