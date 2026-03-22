using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing;

internal class ClassificationPipelineTests
{
    [Test]
    [DisplayName("Uses provisional media type when classification succeeds without explicit result")]
    public async Task Given_NoClassifierMatches_When_ProcessItemAsync_Then_UsesProvisionalMediaType()
    {
        var item = IndexingTestItemBuilder.ForMarkdown("sample.md").WithContent("text").Build();
        var pipeline = new ClassificationPipeline(Array.Empty<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>>());

        var status = await pipeline.ProcessItemAsync(item, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        item.MediaType.Should().NotBeNull("extension-based fallback keeps classification usable when no processor matches");
        item.MediaType!.ToString().Should().Be(item.RawArtifact.ProvisionalMediaType.Value?.ToString());
    }

    [Test]
    [DisplayName("Does not apply provisional media type when classification filters")]
    public async Task Given_ClassifierFilters_When_ProcessItemAsync_Then_DoesNotSetFallbackMediaType()
    {
        var item = IndexingTestItemBuilder.ForMarkdown("sample.md").WithContent("text").Build();
        var pipeline = new ClassificationPipeline(new IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>[]
        {
            new FilteringClassifier()
        });

        var status = await pipeline.ProcessItemAsync(item, CancellationToken.None);

        status.Should().Be(PipelineResult.Filtered);
        item.MediaType.Should().BeNull("filtered items should not be classified or indexed");
    }

    private sealed class FilteringClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    {
        public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IDiscoveredArtifact item,
            CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
            CancellationToken token)
        {
            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Filtered));
        }
    }
}
