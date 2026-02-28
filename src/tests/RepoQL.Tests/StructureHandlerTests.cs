using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Tests;

internal sealed class StructureHandlerTests
{
    [Test]
    public async Task StructureHandler_Formats_Output_With_Headline_And_Structure()
    {
        var documents = new[]
        {
            new ReadDocument(
                "file:///src/A.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: "A.cs | A",
                Summary: null,
                Structure: "+class A\n  +void Run()    #symbol=Run"),
            new ReadDocument(
                "file:///src/B.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: "B.cs | B",
                Summary: null,
                Structure: "+class B")
        };

        var handler = new StructureHandler();

        var result = await handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        var expected = "file:///src/A.cs\nA.cs | A\n\n+class A\n  +void Run()    #symbol=Run\n\nfile:///src/B.cs\nB.cs | B\n\n+class B";

        result.Content.Should().Be(expected);
        result.TokenCount.Should().Be(TokenEstimator.EstimateTokens(expected));
        result.TotalAvailable.Should().Be(documents.Length);
        result.Shown.Should().Be(documents.Length);
        result.ExceedsBudget.Should().BeFalse();
        result.Metadata.FilesConsulted.Should().Equal(documents.Select(doc => doc.Uri));
    }

    [Test]
    public async Task StructureHandler_Notes_Missing_Structure_And_Flags_Budget()
    {
        var document = new ReadDocument(
            "file:///docs/readme.md",
            TextContent: null,
            MediaType: "text/markdown",
            Headline: "readme.md | Readme",
            Summary: null,
            Structure: null);

        var handler = new StructureHandler();

        var result = await handler.ExecuteAsync(
            new[] { document },
            parameter: null,
            tokenBudget: 1,
            ct: CancellationToken.None);

        var expected = "file:///docs/readme.md\nreadme.md | Readme\n(structure not available for this format)";

        result.Content.Should().Be(expected);
        result.TokenCount.Should().BeGreaterThan(1);
        result.ExceedsBudget.Should().BeTrue();
    }
}
