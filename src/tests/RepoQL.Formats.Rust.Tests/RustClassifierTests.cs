using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Rust.Tests;

public sealed class RustClassifierTests
{
    [Test]
    [Arguments("lib.rs", "code.rust")]
    [Arguments("build.rs", "code.rust.build")]
    [Arguments("src/build.rs", "code.rust.build")]
    public async Task Classifier_MapsRustKinds(string fileName, string expectedKind)
    {
        var classifier = new RustClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns(fileName);

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be(expectedKind);
    }

    [Test]
    public async Task Classifier_PassesThroughNonRustFiles()
    {
        var classifier = new RustClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("README.md");
        var nextCalled = false;

        var (result, status) = await classifier.ProcessAsync(
            item,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(RepoQL.Contracts.SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeNull();
        status.Should().Be(PipelineResult.Success);
    }

    private static Task<(RepoQL.Contracts.SemanticMediaType?, PipelineResult)> Next(IDiscoveredArtifact _)
        => Task.FromResult<(RepoQL.Contracts.SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
}
