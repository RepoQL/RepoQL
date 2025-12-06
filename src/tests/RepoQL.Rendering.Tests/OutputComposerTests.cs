using AwesomeAssertions;

namespace RepoQL.Rendering.Tests;

public class OutputComposerTests
{
    [Test]
    [DisplayName("Empty decisions returns empty string")]
    public void Given_EmptyDecisions_Then_ReturnsEmpty()
    {
        var result = new DecisionResult(Array.Empty<RenderingDecision>(), 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Single compact item formats correctly")]
    public void Given_SingleCompactItem_Then_FormatsCorrectly()
    {
        var xrayResult = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null);
        var decision = new RenderingDecision(xrayResult, Representation.Compact, 10);
        var result = new DecisionResult([decision], 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs\nAuth service");
    }

    [Test]
    [DisplayName("Multiple compact items pack tight (no blank lines)")]
    public void Given_MultipleCompactItems_Then_PackTight()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
            new RenderingDecision(
                new XrayResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // Should have only single newline between items
        output.Should().Be(
            " 90% file:///a.cs\n" +
            "A\n" +
            " 80% file:///b.cs\n" +
            "B");
    }

    [Test]
    [DisplayName("Multi-line items have blank lines around them")]
    public void Given_MultilineItems_Then_BlankLinesAround()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", "- Line1\n- Line2", null, null),
                Representation.Standard, 20),
            new RenderingDecision(
                new XrayResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // Should have blank line after multi-line item
        output.Should().Contain("- Line2\n\n 80%");
    }

    [Test]
    [DisplayName("Rich item with snippet is multi-line")]
    public void Given_RichWithSnippet_Then_IsMultiline()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 95, "method", null, null, "code here", "csharp"),
                Representation.Rich, 30),
            new RenderingDecision(
                new XrayResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // Should have blank line after rich item
        output.Should().Contain("```\n\n 80%");
    }

    [Test]
    [DisplayName("Truncation summary appears at end with type breakdown")]
    public void Given_OmittedItems_Then_ShowsTruncationSummary()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var omittedByType = new Dictionary<string, int>
        {
            ["code.csharp"] = 3,
            ["markdown.doc"] = 2
        };
        var result = new DecisionResult(decisions, 5, omittedByType);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Contain("[More: 3x code.csharp, 2x markdown.doc]");
    }

    [Test]
    [DisplayName("Truncation after multi-line has blank line before it")]
    public void Given_TruncationAfterMultiline_Then_BlankLineBefore()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 95, null, null, null, "code", "cs"),
                Representation.Rich, 30),
        };
        var omittedByType = new Dictionary<string, int> { ["code.csharp"] = 3 };
        var result = new DecisionResult(decisions, 3, omittedByType);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Contain("```\n\n[More:");
    }

    [Test]
    [DisplayName("Truncation after compact has no blank line")]
    public void Given_TruncationAfterCompact_Then_NoBlankLine()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 2, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Contain("A\n[More: 2]");
    }

    [Test]
    [DisplayName("Without confidence omits scores")]
    public void Given_NoConfidence_Then_OmitsScores()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: false);

        output.Should().NotContain("90%");
        output.Should().Be("file:///a.cs\nA");
    }

    [Test]
    [DisplayName("Standard without structure is single-line")]
    public void Given_StandardNoStructure_Then_SingleLine()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Standard, 10),
            new RenderingDecision(
                new XrayResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Standard, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // No blank lines between single-line Standard items
        output.Should().NotContain("\n\n");
    }

    [Test]
    [DisplayName("Status footer shown with pending files")]
    public void Given_PendingFiles_Then_ShowsStatusFooter()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var omittedByType = new Dictionary<string, int> { ["code.csharp"] = 5 };
        var result = new DecisionResult(decisions, 5, omittedByType);
        var indexerStatus = new IndexerStatus(IndexPending: 5, SemanticReady: false, SemanticEnabled: true, ElapsedMs: 150);

        var output = OutputComposer.Compose(result, showConfidence: true, indexerStatus);

        output.Should().Contain("[150ms | index: 5 pending | semantic: pending]");
    }

    [Test]
    [DisplayName("Status footer shown when ready")]
    public void Given_Ready_Then_ShowsStatusFooter()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new XrayResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);
        var indexerStatus = new IndexerStatus(IndexPending: 0, SemanticReady: true, SemanticEnabled: true, ElapsedMs: 50);

        var output = OutputComposer.Compose(result, showConfidence: true, indexerStatus);

        output.Should().Contain("[50ms | index: ready | semantic: ready]");
    }
}
