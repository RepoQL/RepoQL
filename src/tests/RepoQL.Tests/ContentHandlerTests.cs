using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Tests;

/// <summary>
/// Purpose: Verifies content modifier output, metadata, and budget flags.
/// Complexity: Uses representative ReadDocument inputs to validate formatting without full indexing.
/// </summary>
internal sealed class ContentHandlerTests
{
    [Test]
    public async Task ContentHandler_Renders_LineNumbers_And_Metadata()
    {
        var handler = new ContentHandler();
        var documents = new[]
        {
            new ReadDocument(
                Uri: "file:///repo/sample.txt",
                TextContent: "first\nsecond",
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await handler.ExecuteAsync(documents, parameter: null, tokenBudget: 10_000, ct: CancellationToken.None);

        result.Content.Should().Contain("--- file:///repo/sample.txt ---");
        result.Content.Should().Contain(" 1: first");
        result.Content.Should().Contain(" 2: second");
        result.ExceedsBudget.Should().BeFalse();
        result.TotalAvailable.Should().Be(1);
        result.Shown.Should().Be(1);

        result.Metadata.FilesConsulted.Should().ContainSingle().Which.Should().Be("file:///repo/sample.txt");
        result.Metadata.Extra["file_count"].Should().BeOfType<int>().Which.Should().Be(1);
        result.Metadata.Extra["total_lines"].Should().BeOfType<int>().Which.Should().Be(2);

        var expectedTokens = TokenEstimator.EstimateTokens(result.Content);
        result.TokenCount.Should().Be(expectedTokens);
    }

    [Test]
    public async Task ContentHandler_Reports_NoContent()
    {
        var handler = new ContentHandler();
        var documents = new[]
        {
            new ReadDocument(
                Uri: "file:///repo/empty.txt",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await handler.ExecuteAsync(documents, parameter: null, tokenBudget: 200, ct: CancellationToken.None);

        result.Content.Should().Contain("--- file:///repo/empty.txt ---");
        result.Content.Should().Contain("file:///repo/empty.txt (no content available)");
        result.Metadata.Extra["total_lines"].Should().BeOfType<int>().Which.Should().Be(0);
    }

    [Test]
    public async Task ContentHandler_Flags_Binary_Files()
    {
        var handler = new ContentHandler();
        var documents = new[]
        {
            new ReadDocument(
                Uri: "file:///repo/data.bin",
                TextContent: "ignored",
                MediaType: "application/octet-stream",
                Headline: "data.bin | 24 B",
                Summary: null,
                Structure: null)
        };

        var result = await handler.ExecuteAsync(documents, parameter: null, tokenBudget: 200, ct: CancellationToken.None);

        result.Content.Should().Contain("--- file:///repo/data.bin ---");
        result.Content.Should().Contain("file:///repo/data.bin (binary file");
        result.Metadata.Extra["total_lines"].Should().BeOfType<int>().Which.Should().Be(0);
    }

    [Test]
    public async Task ContentHandler_Sets_ExceedsBudget_When_Tokens_Over()
    {
        var handler = new ContentHandler();
        var documents = new[]
        {
            new ReadDocument(
                Uri: "file:///repo/large.txt",
                TextContent: "alpha\nbeta\ngamma",
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await handler.ExecuteAsync(documents, parameter: null, tokenBudget: 1, ct: CancellationToken.None);

        result.ExceedsBudget.Should().BeTrue();
        result.TokenCount.Should().BeGreaterThan(1);
    }
}
