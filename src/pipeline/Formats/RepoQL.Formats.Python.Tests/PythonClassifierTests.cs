using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Python.Tests;

public sealed class PythonClassifierTests
{
    [Test]
    [Arguments("app.py", "code.python")]
    [Arguments("app.pyw", "code.python")]
    [Arguments("types.pyi", "code.python.stub")]
    public async Task Classifier_MapsKnownExtensions(string fileName, string expectedKind)
    {
        var classifier = new RepoQL.Formats.Python.PythonClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns(fileName);

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be(expectedKind);
    }

    [Test]
    public async Task Classifier_UnrecognizedExtension_CallsNext()
    {
        var classifier = new RepoQL.Formats.Python.PythonClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("notes.txt");
        var nextCalled = false;

        var (result, status) = await classifier.ProcessAsync(
            item,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeNull();
        status.Should().Be(PipelineResult.Success);
    }

    private static Task<(SemanticMediaType?, PipelineResult)> Next(IDiscoveredArtifact _)
        => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
}
