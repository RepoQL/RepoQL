using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Cpp.TreeSitter;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppParserTests
{
    [Test]
    public async Task Parser_PassesThroughUnsupportedKinds()
    {
        using var materializer = new CppMaterializer(client: new CppTreeSitterClient());
        var parser = new CppParser(materializer);
        var item = A.Fake<IClassifiedArtifact>();
        A.CallTo(() => item.MediaType).Returns(SemanticMediaType.Create("text", "plain").WithKind("code.markdown"));

        var nextCalled = false;
        var expected = Records.Empty;
        var (result, status) = await parser.ProcessAsync(
            item,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(Records?, PipelineResult)>((expected, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        status.Should().Be(PipelineResult.Success);
        result.Should().BeSameAs(expected);
    }

    [Test]
    public async Task Parser_ReturnsError_WhenReadFails()
    {
        using var materializer = new CppMaterializer(client: new CppTreeSitterClient());
        var parser = new CppParser(materializer);
        var item = A.Fake<IClassifiedArtifact>();
        A.CallTo(() => item.MediaType).Returns(SemanticMediaType.Create("text", "plain").WithKind("code.cpp"));
        A.CallTo(() => item.Name).Returns("broken.cpp");
        A.CallTo(() => item.Uri).Returns(RepoUri.Parse("file:///broken.cpp"));
        A.CallTo(() => item.CreateReadStream()).Throws<IOException>();

        var nextCalled = false;
        var (result, status) = await parser.ProcessAsync(
            item,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(Records?, PipelineResult)>((Records.Empty, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeFalse();
        status.Should().Be(PipelineResult.Error);
        result.Should().BeNull();
    }
}
