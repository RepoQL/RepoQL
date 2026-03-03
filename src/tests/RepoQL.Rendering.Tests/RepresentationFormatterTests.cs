using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Rendering.Tests;

public class RepresentationFormatterTests
{
    // Minimal format tests (uri + headline)

    [Test]
    [DisplayName("Minimal shows uri and headline")]
    public void Given_Minimal_Then_ShowsUriAndHeadline()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service - handles authentication",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("file:///src/Auth.cs  Auth service - handles authentication");
        output.Should().NotContain("85%");
    }

    [Test]
    [DisplayName("Minimal truncates headline at newline")]
    public void Given_MinimalWithMultilineHeadline_Then_TruncatesAtNewline()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "First line\nSecond line\nThird line",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("file:///src/Auth.cs  First line");
    }

    [Test]
    [DisplayName("Minimal without headline uses filename")]
    public void Given_MinimalNoHeadline_Then_UsesFilename()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth/JwtService.cs",
            Confidence: 50,
            Kind: null,
            Headline: null,
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("file:///src/Auth/JwtService.cs  JwtService.cs");
    }

    [Test]
    [DisplayName("Minimal extracts filename ignoring fragment")]
    public void Given_MinimalWithFragment_Then_ExtractsFilenameWithoutFragment()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs#line=42,58",
            Confidence: 90,
            Kind: "method",
            Headline: null,
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("file:///src/Auth.cs#line=42,58  Auth.cs");
    }

    [Test]
    [DisplayName("Minimal does not show provenance")]
    public void Given_MinimalWithProvenance_Then_OmitsProvenance()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null,
            Provenance: "semantic");

        var output = RepresentationFormatter.FormatMinimal(result);

        output.Should().Be("file:///src/Auth.cs  Auth service");
        output.Should().NotContain("(semantic)");
    }

    // Intent-aware headline density tests

    [Test]
    [DisplayName("Inventory headline keeps description and token estimate")]
    public void Given_InventoryHeadline_Then_KeepDescriptionAndTokens()
    {
        var headline = "Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale";

        var output = RepresentationFormatter.InventoryHeadline(headline);

        output.Should().Be("Confidence normalizer | ~1.2k tok");
    }

    [Test]
    [DisplayName("Locate headline keeps type and first three sections")]
    public void Given_LocateHeadline_Then_KeepTypeTokensAndFirstThreeSections()
    {
        var headline = "Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale";

        var output = RepresentationFormatter.LocateHeadline(headline);

        output.Should().Be("Confidence normalizer | code.csharp.class | ~1.2k tok | Normalize, Clamp, Weight");
    }

    [Test]
    [DisplayName("Compact applies inventory intent headline trimming")]
    public void Given_CompactInventoryIntent_Then_UsesInventoryHeadlineDensity()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Search/ConfidenceNormalizer.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true, intent: Intent.Inventory);

        output.Should().Be(" 85% file:///src/Search/ConfidenceNormalizer.cs  Confidence normalizer | ~1.2k tok");
    }

    [Test]
    [DisplayName("Compact inspect intent keeps full headline")]
    public void Given_CompactInspectIntent_Then_KeepsFullHeadline()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Search/ConfidenceNormalizer.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true, intent: Intent.Inspect);

        output.Should().Be(" 85% file:///src/Search/ConfidenceNormalizer.cs  Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale");
    }

    // Compact format tests

    [Test]
    [DisplayName("Compact with confidence shows confidence, uri, and headline")]
    public void Given_CompactWithConfidence_Then_FormatsCorrectly()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs  Auth service");
    }

    [Test]
    [DisplayName("Compact without confidence omits confidence")]
    public void Given_CompactWithoutConfidence_Then_OmitsConfidence()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: false);

        output.Should().Be("file:///src/Auth.cs  Auth service");
    }

    [Test]
    [DisplayName("Compact with kind shows kind badge")]
    public void Given_CompactWithKind_Then_ShowsKindBadge()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs#line=42",
            Confidence: 90,
            Kind: "method",
            Headline: "ValidateToken",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 90% file:///src/Auth.cs#line=42  ValidateToken");
    }

    [Test]
    [DisplayName("Compact truncates headline to single line")]
    public void Given_CompactWithMultilineHeadline_Then_TruncatesAtNewline()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "First line\nSecond line",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs  First line");
    }

    [Test]
    [DisplayName("Compact without headline shows only uri line")]
    public void Given_CompactNoHeadline_Then_OnlyUri()
    {
        var result = new ExploreResult(
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

    [Test]
    [DisplayName("Compact does not show provenance")]
    public void Given_CompactWithProvenance_Then_OmitsProvenance()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null,
            Provenance: "semantic");

        var output = RepresentationFormatter.FormatCompact(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs  Auth service");
        output.Should().NotContain("(semantic)");
    }

    [Test]
    [DisplayName("Compact child fragment with symbol uses simple symbol only")]
    public void Given_CompactChildWithLineAndSymbol_Then_ShowsSimpleSymbolFragment()
    {
        var result = new ExploreResult(
            Uri: "file:///src/RepoQL.Explore/Search/ConfidenceNormalizer.cs#line=50,71&symbol=RepoQL.Explore.Search.ConfidenceNormalizer.NormalizeResult",
            Confidence: 92,
            Kind: null,
            Headline: "Normalize result",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatCompact(
            result,
            showConfidence: true,
            parentUri: "file:///src/RepoQL.Explore/Search/ConfidenceNormalizer.cs");

        output.Should().Be(" 92% Normalize result #symbol=NormalizeResult");
        output.Should().NotContain("#line=");
        output.Should().NotContain("RepoQL.Explore.Search.ConfidenceNormalizer.");
    }

    // Standard format tests

    [Test]
    [DisplayName("Standard includes structure")]
    public void Given_Standard_Then_IncludesStructure()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "AuthController - 8 endpoints",
            Structure: "- Login, Logout\n- Register",
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatStandard(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs  AuthController - 8 endpoints\n  - Login, Logout\n  - Register");
    }

    [Test]
    [DisplayName("Standard without structure shows headline only")]
    public void Given_StandardNoStructure_Then_ShowsHeadline()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 70,
            Kind: null,
            Headline: "Auth module",
            Structure: null,
            Snippet: null,
            Lang: null);

        var output = RepresentationFormatter.FormatStandard(result, showConfidence: true);

        output.Should().Be(" 70% file:///src/Auth.cs  Auth module");
    }

    [Test]
    [DisplayName("Standard appends provenance after headline")]
    public void Given_StandardWithProvenance_Then_ShowsProvenanceTag()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 70,
            Kind: null,
            Headline: "Auth module",
            Structure: null,
            Snippet: null,
            Lang: null,
            Provenance: "semantic");

        var output = RepresentationFormatter.FormatStandard(result, showConfidence: true);

        output.Should().Be(" 70% file:///src/Auth.cs  Auth module (semantic)");
    }

    // Rich format tests

    [Test]
    [DisplayName("Rich shows snippet in code fence")]
    public void Given_Rich_Then_ShowsCodeFence()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs#line=42",
            Confidence: 98,
            Kind: "method",
            Headline: "ValidateToken",  // Should be ignored
            Structure: null,
            Snippet: "public bool Validate() { return true; }",
            Lang: "csharp");

        var output = RepresentationFormatter.FormatRich(result, showConfidence: true);

        output.Should().Be(" 98% file:///src/Auth.cs#line=42\n```csharp\npublic bool Validate() { return true; }\n```");
    }

    [Test]
    [DisplayName("Rich without snippet shows only uri")]
    public void Given_RichNoSnippet_Then_ShowsOnlyUri()
    {
        var result = new ExploreResult(
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
        var result = new ExploreResult(
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

    [Test]
    [DisplayName("Rich appends provenance to header line")]
    public void Given_RichWithProvenance_Then_ShowsProvenanceTag()
    {
        var result = new ExploreResult(
            Uri: "file:///src/Auth.cs#line=42",
            Confidence: 98,
            Kind: "method",
            Headline: "ValidateToken",
            Structure: null,
            Snippet: "public bool Validate() { return true; }",
            Lang: "csharp",
            Provenance: "name");

        var output = RepresentationFormatter.FormatRich(result, showConfidence: true);

        output.Should().Be(" 98% file:///src/Auth.cs#line=42 (name)\n```csharp\npublic bool Validate() { return true; }\n```");
    }

    // Confidence formatting tests

    [Test]
    [DisplayName("Confidence is right-aligned with % suffix")]
    public void Given_VariousConfidences_Then_RightAligned()
    {
        var result5 = new ExploreResult("file:///a.cs", 5, null, "Headline", null, null, null);
        var result50 = new ExploreResult("file:///a.cs", 50, null, "Headline", null, null, null);
        var result100 = new ExploreResult("file:///a.cs", 100, null, "Headline", null, null, null);

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
    [DisplayName("Status footer shows healthy compact format")]
    public void Given_HealthyStatus_Then_ShowsCompactFooter()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 42);

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500);

        output.Should().Be("[1.5k tok | 42 ms | index: ready | semantic: ready]");
        output.Split(" | ").Length.Should().BeLessThanOrEqualTo(4);
    }

    [Test]
    [DisplayName("Status footer shows quality and coverage first when provided")]
    public void Given_QualityAndCoverage_Then_FooterFrontLoadsSignals()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 42)
        {
            SearchQualityTier = "strong",
            CoverageAboveThreshold = 12,
            CoverageTotalDocuments = 40
        };

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500);

        output.Should().Be("[quality: strong | 12 of 40 above threshold | 1.5k tok | 42 ms | index: ready | semantic: ready]");
    }

    [Test]
    [DisplayName("Status footer shows all-in-scope coverage format")]
    public void Given_AllDocumentsAboveThreshold_Then_ShowsAllInScopeCoverage()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 42)
        {
            SearchQualityTier = "moderate",
            CoverageAboveThreshold = 40,
            CoverageTotalDocuments = 40,
            CoverageAllInScope = true
        };

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500);

        output.Should().Be("[quality: moderate | 40 matches (all in scope) | 1.5k tok | 42 ms | index: ready | semantic: ready]");
    }

    [Test]
    [DisplayName("Status footer shows pending percentage")]
    public void Given_PendingFiles_Then_ShowsPercentageAndPendingCount()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 5,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: false,
            SemanticPercent: 72,
            ExecutionTimeMs: 150);

        var output = RepresentationFormatter.FormatStatusFooter(status);

        output.Should().Be("[150 ms | index: 95% (5 pending) | semantic: 72%]");
    }

    [Test]
    [DisplayName("Status footer falls back to legacy pending format when totals are unknown")]
    public void Given_UnknownTotals_Then_UsesPendingFallback()
    {
        var status = new TrustSignal(
            IndexTotal: 0,
            IndexPending: 5,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: false,
            SemanticPercent: 0,
            ExecutionTimeMs: 150);

        var output = RepresentationFormatter.FormatStatusFooter(status);

        output.Should().Be("[150 ms | index: 5 pending | semantic: pending]");
    }

    [Test]
    [DisplayName("Status footer shows disabled when embeddings off")]
    public void Given_EmbeddingsDisabled_Then_ShowsDisabled()
    {
        var status = new TrustSignal(
            IndexTotal: 10,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: false,
            SemanticReady: false,
            SemanticPercent: 0,
            ExecutionTimeMs: 30);

        var output = RepresentationFormatter.FormatStatusFooter(status);

        output.Should().Be("[30 ms | index: ready | semantic: disabled]");
    }

    [Test]
    [DisplayName("Status footer appends failed count only when non-zero")]
    public void Given_FailedFiles_Then_ShowsFailedCount()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 3,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 35);

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1200);

        output.Should().Be("[1.2k tok | 35 ms | index: ready | semantic: ready | 3 failed]");
    }

    [Test]
    [DisplayName("Status footer appends stale count only when non-zero")]
    public void Given_StaleFiles_Then_ShowsStaleCount()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 12,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 42);

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500);

        output.Should().Be("[1.5k tok | 42 ms | index: ready | semantic: ready | stale: 12]");
    }

    [Test]
    [DisplayName("Status footer shows NOT READY during discovery")]
    public void Given_DiscoveryInProgress_Then_ShowsNotReadyFooter()
    {
        var status = new TrustSignal(
            IndexTotal: 847,
            IndexPending: 847,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: false,
            SemanticPercent: 0,
            ExecutionTimeMs: 12);

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500);

        output.Should().Be("[NOT READY - 847 pending, discovery in progress]");
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
        var result = new ExploreResult(
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

        minimal.Should().Be("file:///test.cs  Test");  // Minimal is uri + headline
        standard.Should().Contain("- Item");  // Standard shows structure
        standard.Should().Contain("file:///test.cs");  // Has URI
    }

    // Representation hint tests

    [Test]
    [DisplayName("Representation hint returns null for full representation")]
    public void Given_FullRepresentation_Then_ReturnsNull()
    {
        var costs = new RepresentationCosts(FullTokens: 1000, StructureTokens: 500, HeadlineTokens: 100);

        var hint = RepresentationFormatter.FormatRepresentationHint("full", costs);

        hint.Should().BeNull();
    }

    [Test]
    [DisplayName("Representation hint shows structure cost for headline level")]
    public void Given_HeadlineLevel_Then_ShowsStructureAndFullCosts()
    {
        var costs = new RepresentationCosts(FullTokens: 5200, StructureTokens: 1500, HeadlineTokens: 100);

        var hint = RepresentationFormatter.FormatRepresentationHint("headline", costs);

        hint.Should().NotBeNull();
        hint.Should().Be("showing: headline | structure: 1.5k tok | full: 5.2k tok");
    }

    [Test]
    [DisplayName("Representation hint shows full cost for structure level")]
    public void Given_StructureLevel_Then_ShowsFullCost()
    {
        var costs = new RepresentationCosts(FullTokens: 5200, StructureTokens: 1500, HeadlineTokens: 100);

        var hint = RepresentationFormatter.FormatRepresentationHint("structure", costs);

        hint.Should().NotBeNull();
        hint.Should().Be("showing: structure | full: 5.2k tok");
    }

    [Test]
    [DisplayName("Representation hint handles missing costs")]
    public void Given_MissingCosts_Then_ShowsOnlyAvailableCosts()
    {
        // Only full cost available
        var costs = new RepresentationCosts(FullTokens: 3000, StructureTokens: null, HeadlineTokens: 50);

        var hint = RepresentationFormatter.FormatRepresentationHint("headline", costs);

        hint.Should().NotBeNull();
        hint.Should().Be("showing: headline | full: 3.0k tok");
    }

    [Test]
    [DisplayName("Representation hint returns null when no higher-fidelity costs available")]
    public void Given_NoHigherFidelityCosts_Then_ReturnsNull()
    {
        // At structure level but no full cost available
        var costs = new RepresentationCosts(FullTokens: null, StructureTokens: 1500, HeadlineTokens: 100);

        var hint = RepresentationFormatter.FormatRepresentationHint("structure", costs);

        // No higher-fidelity representation info to show
        hint.Should().BeNull();
    }

    [Test]
    [DisplayName("Representation hint formats small token counts without k suffix")]
    public void Given_SmallTokenCounts_Then_FormatsWithoutKSuffix()
    {
        var costs = new RepresentationCosts(FullTokens: 500, StructureTokens: 200, HeadlineTokens: 50);

        var hint = RepresentationFormatter.FormatRepresentationHint("headline", costs);

        hint.Should().NotBeNull();
        hint.Should().Be("showing: headline | structure: 200 tok | full: 500 tok");
    }

    [Test]
    [DisplayName("Representation hint for none level shows all available costs")]
    public void Given_NoneLevel_Then_ShowsAllAvailableCosts()
    {
        var costs = new RepresentationCosts(FullTokens: 5000, StructureTokens: 1500, HeadlineTokens: 100);

        var hint = RepresentationFormatter.FormatRepresentationHint("none", costs);

        hint.Should().NotBeNull();
        hint.Should().Be("showing: none | headline: 100 tok | structure: 1.5k tok | full: 5.0k tok");
    }

    [Test]
    [DisplayName("Status footer integrates representation hint")]
    public void Given_StatusFooterWithHint_Then_IncludesHintInBrackets()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 50);
        var hint = "showing: structure | full: 5.2k tok";

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500, representationHint: hint);

        output.Should().Be("[1.5k tok | 50 ms | index: ready | semantic: ready | showing: structure | full: 5.2k tok]");
    }

    [Test]
    [DisplayName("Status footer without hint works as before")]
    public void Given_StatusFooterWithoutHint_Then_NoExtraPipe()
    {
        var status = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 50);

        var output = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 1500, representationHint: null);

        output.Should().Be("[1.5k tok | 50 ms | index: ready | semantic: ready]");
    }
}
