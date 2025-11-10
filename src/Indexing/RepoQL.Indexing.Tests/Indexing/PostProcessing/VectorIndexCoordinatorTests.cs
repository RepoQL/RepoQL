using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal class VectorIndexCoordinatorTests
{
    [Test]
    [DisplayName("Vector refresh only runs once per epoch until invalidated")]
    public async Task Given_VectorCoordinator_When_ApplyAsyncTwice_Then_RefreshesOnce()
    {
        var refresher = new FakeRefresher();
        var coordinator = new VectorIndexCoordinator(refresher, NullLogger<VectorIndexCoordinator>.Instance);
        var item = new IndexingTestItemBuilder()
            .WithUri("file:///repo/vector.md")
            .WithContent("text")
            .Build();
        item.SetEpoch(0);

        await coordinator.ApplyAsync(item, CancellationToken.None);
        await coordinator.ApplyAsync(item, CancellationToken.None);
        refresher.Invocations.Should().Be(1);

        await coordinator.ApplyDeletesAsync(new[] { RepoUri.Parse("file:///repo/vector.md") }, CancellationToken.None);
        await coordinator.ApplyAsync(item, CancellationToken.None);
        refresher.Invocations.Should().Be(2);
    }
}
