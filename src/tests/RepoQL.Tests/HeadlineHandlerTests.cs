using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class HeadlineHandlerTests
{
    [Test]
    public async Task BuildsLinesAndMetadata()
    {
        var documents = new[]
        {
            new ReadDocument("file:///repo/a.cs", null, null, "Alpha", null, null),
            new ReadDocument("file:///repo/b.cs", null, null, null, null, null)
        };
        var handler = new HeadlineHandler();

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be("file:///repo/a.cs | Alpha\nfile:///repo/b.cs | (no headline available)");
        result.TotalAvailable.Should().Be(2);
        result.Shown.Should().Be(2);
        result.Metadata.FilesConsulted.Should().BeEquivalentTo(new[] { "file:///repo/a.cs", "file:///repo/b.cs" });
        result.Metadata.Warning.Should().BeNull();
    }

    [Test]
    public async Task ComputesTokensAndBudgetFlag()
    {
        var documents = new[]
        {
            new ReadDocument("file:///repo/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new HeadlineHandler();

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: 1, CancellationToken.None);

        var expectedTokens = TokenEstimator.EstimateTokens(result.Content);
        result.TokenCount.Should().Be(expectedTokens);
        result.ExceedsBudget.Should().Be(expectedTokens > 1);
    }

    [Test]
    public async Task NoMatches_ReturnsMessage()
    {
        var handler = new HeadlineHandler();

        var result = await handler.ExecuteAsync(Array.Empty<ReadDocument>(), null, tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be("No files matched.");
        result.TotalAvailable.Should().Be(0);
        result.Shown.Should().Be(0);
    }
}
