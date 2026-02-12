using AwesomeAssertions;
using RepoQL.ConsoleApp.Host;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class SimilarHandlerTests
{
    [Test]
    [DisplayName("SimilarHandler returns usage when parameter is null")]
    public async Task SimilarHandler_NullParameter_ReturnsUsage()
    {
        var handler = new SimilarHandler(null!, null);
        handler.CanHandle("similar").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "content", "text/plain", null, null, null)
            ],
            null,
            1000,
            CancellationToken.None);

        result.Content.Should().Contain("Usage:");
    }

    [Test]
    [DisplayName("SimilarHandler returns usage when parameter is empty")]
    public async Task SimilarHandler_EmptyParameter_ReturnsUsage()
    {
        var handler = new SimilarHandler(null!, null);

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "content", "text/plain", null, null, null)
            ],
            "   ",
            1000,
            CancellationToken.None);

        result.Content.Should().Contain("Usage:");
    }

    [Test]
    [DisplayName("SimilarHandler returns no files matched for empty documents")]
    public async Task SimilarHandler_NoDocuments_ReturnsNoFilesMatched()
    {
        var handler = new SimilarHandler(null!, null);

        var result = await handler.ExecuteAsync(
            [],
            "file:///src/Seed.cs",
            1000,
            CancellationToken.None);

        result.Content.Should().Be("No files matched pattern.");
    }

    [Test]
    [DisplayName("SimilarHandler does not handle unrelated modifiers")]
    public void SimilarHandler_DoesNotHandleOtherModifiers()
    {
        var handler = new SimilarHandler(null!, null);

        handler.CanHandle("similar").Should().BeTrue();
        handler.CanHandle("find").Should().BeFalse();
        handler.CanHandle("grep").Should().BeFalse();
        handler.CanHandle(null).Should().BeFalse();
    }
}
