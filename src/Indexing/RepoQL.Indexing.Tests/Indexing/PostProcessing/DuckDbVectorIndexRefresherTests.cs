using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal class DuckDbVectorIndexRefresherTests
{
    [Test]
    [DisplayName("Refresh computes embeddings for stored documents")]
    public async Task Given_Document_When_RefreshAsync_Then_EmbeddingProviderInvoked()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///repo/vector-doc.md");

        var provider = new RecordingEmbeddingProvider();
        var refresher = new DuckDbVectorIndexRefresher(new SingleConnectionFactory(store.Connection), provider, NullLogger<DuckDbVectorIndexRefresher>.Instance);

        await refresher.RefreshAsync(CancellationToken.None);

        provider.EmbedCount.Should().Be(1);
    }
}
