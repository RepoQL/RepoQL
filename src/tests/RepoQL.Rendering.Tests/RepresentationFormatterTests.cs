using AwesomeAssertions;

namespace RepoQL.Rendering.Tests;

public class RepresentationFormatterTests
{
    // Minimal format tests (headline only, no URI)

    [Test]
    [DisplayName("Minimal shows headline only")]
    public void Given_Minimal_Then_ShowsHeadlineOnly()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service - handles authentication",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("Auth service - handles authentication");
        output.Should().NotContain("file:///");
        output.Should().NotContain("85%");
    }

    [Test]
    [DisplayName("Minimal truncates headline at newline")]
    public void Given_MinimalWithMultilineHeadline_Then_TruncatesAtNewline()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "First line\nSecond line\nThird line",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("First line");
    }

    [Test]
    [DisplayName("Minimal without headline uses filename")]
    public void Given_MinimalNoHeadline_Then_UsesFilename()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth/JwtService.cs",
            Confidence: 50,
            Kind: null,
            Headline: null,
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("JwtService.cs");
    }

    [Test]
    [DisplayName("Minimal extracts filename ignoring fragment")]
    public void Given_MinimalWithFragment_Then_ExtractsFilenameWithoutFragment()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs#line=42,58",
            Confidence: 90,
            Kind: "method",
            Headline: null,
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("Auth.cs");
    }

    // Compact format tests

    [Test]
    [DisplayName("Compact with confidence shows confidence, uri, and headline")]
    public void Given_CompactWithConfidence_Then_FormatsCorrectly()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs\nAuth service");
    }

    [Test]
    [DisplayName("Compact without confidence omits confidence")]
    public void Given_CompactWithoutConfidence_Then_OmitsConfidence()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: false);

        output.Should().Be("file:///src/Auth.cs\nAuth service");
    }

    [Test]
    [DisplayName("Compact with kind shows kind badge")]
    public void Given_CompactWithKind_Then_ShowsKindBadge()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs#line=42",
            Confidence: 90,
            Kind: "method",
            Headline: "ValidateToken",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 90% [method] file:///src/Auth.cs#line=42\nValidateToken");
    }

    [Test]
    [DisplayName("Compact truncates headline to single line")]
    public void Given_CompactWithMultilineHeadline_Then_TruncatesAtNewline()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "First line\nSecond line",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs\nFirst line");
    }

    [Test]
    [DisplayName("Compact without headline shows only uri line")]
    public void Given_CompactNoHeadline_Then_OnlyUri()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 50,
            Kind: null,
            Headline: null,
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 50% file:///src/Auth.cs");
    }

    // Standard format tests

    [Test]
    [DisplayName("Standard includes structure")]
    public void Given_Standard_Then_IncludesStructure()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "AuthController - 8 endpoints",
            Structure: "- Login, Logout\n- Register",
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatStandard(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs\nAuthController - 8 endpoints\n- Login, Logout\n- Register");
    }

    [Test]
    [DisplayName("Standard without structure shows headline only")]
    public void Given_StandardNoStructure_Then_ShowsHeadline()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 70,
            Kind: null,
            Headline: "Auth module",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatStandard(result, showConfidence: true);

        output.Should().Be(" 70% file:///src/Auth.cs\nAuth module");
    }

    // Rich format tests

    [Test]
    [DisplayName("Rich shows snippet in code fence")]
    public void Given_Rich_Then_ShowsCodeFence()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs#line=42",
            Confidence: 98,
            Kind: "method",
            Headline: "ValidateToken",  // Should be ignored
            Structure: null,
            Snippet: "public bool Validate() { return true; }",
            Lang: "csharp");

        var output = RepresentationFormatter.FormatRich(result, showConfidence: true);

        output.Should().Be(" 98% [method] file:///src/Auth.cs#line=42\n```csharp\npublic bool Validate() { return true; }\n```");
    }

    [Test]
    [DisplayName("Rich without snippet shows only uri")]
    public void Given_RichNoSnippet_Then_ShowsOnlyUri()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 90,
            Kind: null,
            Headline: "Auth",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatRich(result, showConfidence: true);

        output.Should().Be(" 90% file:///src/Auth.cs");
    }

    [Test]
    [DisplayName("Rich snippet ending with newline preserves it")]
    public void Given_RichSnippetWithNewline_Then_PreservesNewline()
    {
        var result = new XrayResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 80,
            Kind: null,
            Headline: null,
            Structure: null,
            Snippet: "line1\nline2\n",
            Lang: "txt");

        var output = RepresentationFormatter.FormatRich(result, showConfidence: true);

        output.Should().Be(" 80% file:///src/Auth.cs\n```txt\nline1\nline2\n```");
    }

    // Confidence formatting tests

    [Test]
    [DisplayName("Confidence is right-aligned with % suffix")]
    public void Given_VariousConfidences_Then_RightAligned()
    {
        var result5 = new XrayResult("file:///a.cs", 5, null, "Headline", null, null, null);
        var result50 = new XrayResult("file:///a.cs", 50, null, "Headline", null, null, null);
        var result100 = new XrayResult("file:///a.cs", 100, null, "Headline", null, null, null);

        var out5 = RepresentationFormatter.FormatCompact(result5, showConfidence: true);
        var out50 = RepresentationFormatter.FormatCompact(result50, showConfidence: true);
        var out100 = RepresentationFormatter.FormatCompact(result100, showConfidence: true);

        out5.Should().StartWith("  5%");
        out50.Should().StartWith(" 50%");
        out100.Should().StartWith("100%");
    }

    // Truncation summary tests

    [Test]
    [DisplayName("Truncation summary with type breakdown")]
    public void Given_TruncationWithTypes_Then_ShowsTypeBreakdown()
    {
        var omittedByType = new Dictionary<string, int>
        {
            ["code.csharp"] = 15,
            ["markdown.doc"] = 10
        };
        var output = RepresentationFormatter.FormatTruncationSummary(25, omittedByType);

        output.Should().Be("[More: 15x code.csharp, 10x markdown.doc]");
    }

    [Test]
    [DisplayName("Truncation summary without types shows count only")]
    public void Given_TruncationNoTypes_Then_ShowsCountOnly()
    {
        var output = RepresentationFormatter.FormatTruncationSummary(5, null);

        output.Should().Be("[More: 5]");
    }

    [Test]
    [DisplayName("Status footer shows pending when indexing")]
    public void Given_PendingFiles_Then_ShowsPending()
    {
        var status = new IndexerStatus(IndexPending: 5, SemanticReady: false, SemanticEnabled: true, ElapsedMs: 150);

        var output = RepresentationFormatter.FormatStatusFooter(status);

        output.Should().Be("[150ms | index: 5 pending | semantic: pending]");
    }

    [Test]
    [DisplayName("Status footer shows ready when idle")]
    public void Given_NoPending_Then_ShowsReady()
    {
        var status = new IndexerStatus(IndexPending: 0, SemanticReady: true, SemanticEnabled: true, ElapsedMs: 50);

        var output = RepresentationFormatter.FormatStatusFooter(status);

        output.Should().Be("[50ms | index: ready | semantic: ready]");
    }

    [Test]
    [DisplayName("Status footer shows disabled when embeddings off")]
    public void Given_EmbeddingsDisabled_Then_ShowsDisabled()
    {
        var status = new IndexerStatus(IndexPending: 0, SemanticReady: false, SemanticEnabled: false, ElapsedMs: 30);

        var output = RepresentationFormatter.FormatStatusFooter(status);

        output.Should().Be("[30ms | index: ready | semantic: disabled]");
    }

    [Test]
    [DisplayName("Truncation summary limits to 4 types")]
    public void Given_ManyTypes_Then_LimitsTo4AndShowsRemaining()
    {
        var omittedByType = new Dictionary<string, int>
        {
            ["type1"] = 10,
            ["type2"] = 8,
            ["type3"] = 6,
            ["type4"] = 4,
            ["type5"] = 2,
            ["type6"] = 1
        };
        var output = RepresentationFormatter.FormatTruncationSummary(31, omittedByType);

        output.Should().Contain("10x type1");
        output.Should().Contain("8x type2");
        output.Should().Contain("6x type3");
        output.Should().Contain("4x type4");
        output.Should().Contain("+3 other");
        output.Should().NotContain("type5");
        output.Should().NotContain("type6");
    }

    // Format dispatch tests

    [Test]
    [DisplayName("Format dispatches to correct formatter")]
    public void Given_Decision_Then_DispatchesToCorrectFormatter()
    {
        var result = new XrayResult(
            Uri: "file:///test.cs",
            Confidence: 75,
            Kind: null,
            Headline: "Test",
            Structure: "- Item",
            Snippet: null,
            Lang: null);

        var minimalDecision = new RenderingDecision(result, Representation.Minimal, 10);
        var standardDecision = new RenderingDecision(result, Representation.Standard, 20);

        var minimal = RepresentationFormatter.Format(minimalDecision, showConfidence: true);
        var standard = RepresentationFormatter.Format(standardDecision, showConfidence: true);

        minimal.Should().Be("Test");  // Minimal is just headline
        minimal.Should().NotContain("file:///");  // No URI
        standard.Should().Contain("- Item");  // Standard shows structure
        standard.Should().Contain("file:///test.cs");  // Has URI
    }
}
