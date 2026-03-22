using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;

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
        var exploreResult = new ExploreResult(
            Uri: "file:///src/Auth.cs",
            Confidence: 85,
            Kind: null,
            Headline: "Auth service",
            Structure: null,
            Snippet: null,
            Lang: null);
        var decision = new RenderingDecision(exploreResult, Representation.Compact, 10);
        var result = new DecisionResult([decision], 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Be(" 85% file:///src/Auth.cs  Auth service");
    }

    [Test]
    [DisplayName("Multiple compact items pack tight (no blank lines)")]
    public void Given_MultipleCompactItems_Then_PackTight()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
            new RenderingDecision(
                new ExploreResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // Should have only single newline between items
        output.Should().Be(
            " 90% file:///a.cs  A\n" +
            " 80% file:///b.cs  B");
    }

    [Test]
    [DisplayName("Compose preserves compact headline token detail")]
    public void Given_CompactHeadlineWithTokens_Then_ComposeKeepsTokenEstimate()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult(
                    "file:///src/ConfidenceNormalizer.cs",
                    90,
                    null,
                    "Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale",
                    null,
                    null,
                    null),
                Representation.Compact,
                10)
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Be(" 90% file:///src/ConfidenceNormalizer.cs  Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale");
    }

    [Test]
    [DisplayName("Compose preserves child fragment compaction for nested children")]
    public void Given_NestedChildren_Then_ComposeAppliesChildFragmentCompaction()
    {
        var parent = new RenderingDecision(
            new ExploreResult(
                "file:///src/ConfidenceNormalizer.cs",
                90,
                null,
                "Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale",
                null,
                null,
                null),
            Representation.Compact,
            10,
            ChildDecisions:
            [
                new RenderingDecision(
                    new ExploreResult(
                        "file:///src/ConfidenceNormalizer.cs#line=50,71&symbol=RepoQL.Explore.Search.ConfidenceNormalizer.NormalizeResult",
                        88,
                        null,
                        "Normalize result | csharp.method | 300 B, 12 lines | ~60 tok | guard, score, clamp, return",
                        null,
                        null,
                        null),
                    Representation.Compact,
                    8)
            ]);

        var result = new DecisionResult([parent], 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Contain("Confidence normalizer | code.csharp.class | 4.0 KB, 120 lines | ~1.2k tok | Normalize, Clamp, Weight, Scale");
        output.Should().Contain("Normalize result | csharp.method | 300 B, 12 lines | ~60 tok | guard, score, clamp, return #symbol=NormalizeResult");
    }

    [Test]
    [DisplayName("Confidence cliff inserts a single separator")]
    public void Given_ConfidenceCliff_Then_InsertsOneBlankLineSeparator()
    {
        var decisions = new[]
        {
            new RenderingDecision(new ExploreResult("file:///a.cs", 95, null, "A", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///b.cs", 90, null, "B", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///c.cs", 50, null, "C", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///d.cs", 10, null, "D", null, null, null), Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Be(
            " 95% file:///a.cs  A\n" +
            " 90% file:///b.cs  B\n\n" +
            " 50% file:///c.cs  C\n" +
            " 10% file:///d.cs  D");
        output.Should().Contain("\n\n 50%");
        output.Should().NotContain("\n\n\n");
    }

    [Test]
    [DisplayName("Confidence separator is disabled when confidence is hidden")]
    public void Given_ShowConfidenceFalse_Then_NoConfidenceSeparator()
    {
        var decisions = new[]
        {
            new RenderingDecision(new ExploreResult("file:///a.cs", 95, null, "A", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///b.cs", 40, null, "B", null, null, null), Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: false);

        output.Should().Be(
            "file:///a.cs  A\n" +
            "file:///b.cs  B");
    }

    [Test]
    [DisplayName("Multi-line items have blank lines around them")]
    public void Given_MultilineItems_Then_BlankLinesAround()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", "- Line1\n- Line2", null, null),
                Representation.Standard, 20),
            new RenderingDecision(
                new ExploreResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // Should have blank line after multi-line item
        output.Should().Contain("- Line2\n\n 80%");
    }

    [Test]
    [DisplayName("Rich item with multi-line snippet is multi-line")]
    public void Given_RichWithSnippet_Then_IsMultiline()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 95, "method", null, null, "line1\nline2", "csharp"),
                Representation.Rich, 30),
            new RenderingDecision(
                new ExploreResult("file:///b.cs", 80, null, "B", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        // Should have blank line after rich item with code fence
        output.Should().Contain("```\n\n 80%");
    }

    [Test]
    [DisplayName("Truncation summary appears at end with type breakdown")]
    public void Given_OmittedItems_Then_ShowsTruncationSummary()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
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
                new ExploreResult("file:///a.cs", 95, null, null, null, "line1\nline2", "cs"),
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
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 2, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Contain("  A\n[More: 2]");
    }

    [Test]
    [DisplayName("Without confidence omits scores")]
    public void Given_NoConfidence_Then_OmitsScores()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: false);

        output.Should().NotContain("90%");
        output.Should().Be("file:///a.cs  A");
    }

    [Test]
    [DisplayName("Standard without structure is single-line")]
    public void Given_StandardNoStructure_Then_SingleLine()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Standard, 10),
            new RenderingDecision(
                new ExploreResult("file:///b.cs", 80, null, "B", null, null, null),
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
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var omittedByType = new Dictionary<string, int> { ["code.csharp"] = 5 };
        var result = new DecisionResult(decisions, 5, omittedByType);
        var trustSignal = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 5,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: false,
            SemanticPercent: 72,
            ExecutionTimeMs: 150);

        var output = OutputComposer.Compose(result, showConfidence: true, trustSignal);

        output.Should().Contain("150 ms | index: 95% (5 pending) | semantic: 72%]");
    }

    [Test]
    [DisplayName("Status footer shown when ready")]
    public void Given_Ready_Then_ShowsStatusFooter()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);
        var trustSignal = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 50);

        var output = OutputComposer.Compose(result, showConfidence: true, trustSignal);

        output.Should().Contain("50 ms | index: ready | semantic: ready]");
    }

    [Test]
    [DisplayName("Status footer front-loads quality and coverage with actual body token count")]
    public void Given_QualityCoverageSignals_Then_ComposeIncludesSignalsFirst()
    {
        var decisions = new[]
        {
            new RenderingDecision(
                new ExploreResult("file:///a.cs", 90, null, "A", null, null, null),
                Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);
        var trustSignal = new TrustSignal(
            IndexTotal: 100,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 50)
        {
            SearchQualityTier = "weak",
            CoverageAboveThreshold = 3,
            CoverageTotalDocuments = 20
        };

        var body = OutputComposer.Compose(result, showConfidence: true);
        var expectedTokens = TokenEstimator.EstimateTokens($"{body}\n");
        var expectedFooter = RepresentationFormatter.FormatStatusFooter(trustSignal, expectedTokens);
        var output = OutputComposer.Compose(result, showConfidence: true, trustSignal);

        output.Should().EndWith(expectedFooter);
    }

    [Test]
    [DisplayName("Cluster headers render for directory groups with 3+ members")]
    public void Given_ThreeMemberDirectoryGroup_Then_ComposeShowsClusterHeader()
    {
        var decisions = new[]
        {
            new RenderingDecision(new ExploreResult("file:///src/Top.cs", 99, null, "Top", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///src/Auth/AuthService.cs", 95, null, "AuthService", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///src/Auth/JwtHandler.cs", 92, null, "JwtHandler", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///src/Auth/TokenValidator.cs", 90, null, "TokenValidator", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///docs/Guide.md", 80, null, "Guide", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///tests/AuthTests.cs", 70, null, "Tests", null, null, null), Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);

        var output = OutputComposer.Compose(result, showConfidence: true);

        output.Should().Contain("── src/Auth/ (3 results) ──");
    }

    [Test]
    [DisplayName("Footer token accounting uses actual rendered body text")]
    public void Given_ClusterHeader_Then_FooterUsesMeasuredBodyTokens()
    {
        var decisions = new[]
        {
            new RenderingDecision(new ExploreResult("file:///src/Top.cs", 99, null, "Top", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///src/Auth/AuthService.cs", 95, null, "AuthService", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///src/Auth/JwtHandler.cs", 92, null, "JwtHandler", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///src/Auth/TokenValidator.cs", 90, null, "TokenValidator", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///docs/Guide.md", 80, null, "Guide", null, null, null), Representation.Compact, 10),
            new RenderingDecision(new ExploreResult("file:///tests/AuthTests.cs", 70, null, "Tests", null, null, null), Representation.Compact, 10),
        };
        var result = new DecisionResult(decisions, 0, null);
        var trustSignal = new TrustSignal(
            IndexTotal: 10,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 25);

        var body = OutputComposer.Compose(result, showConfidence: true);
        var expectedTokens = TokenEstimator.EstimateTokens($"{body}\n");
        var expectedFooter = RepresentationFormatter.FormatStatusFooter(trustSignal, expectedTokens);
        var output = OutputComposer.Compose(result, showConfidence: true, trustSignal);

        output.Should().EndWith(expectedFooter);
    }

    [Test]
    [DisplayName("Estimated versus actual body tokens stay within tolerance for realistic explore output")]
    public void Given_RealisticExploreOutput_When_ComparingEstimatedVsActual_Then_DriftWithinTolerance()
    {
        var results = CreateRealisticExploreResults();
        var decisions = ValueBasedAllocator.Allocate(results, tokenBudget: 2200, breadth: 5);
        var decisionResult = new DecisionResult(decisions, 0, null);

        var renderedBody = OutputComposer.Compose(decisionResult, showConfidence: true);
        var actualTokens = TokenEstimator.EstimateTokens(renderedBody);
        var estimatedTokens = EstimateDecisionResultTokens(decisionResult);
        var drift = Math.Abs(estimatedTokens - actualTokens);
        var tolerance = Math.Max(100, (int)Math.Round(actualTokens * 0.20, MidpointRounding.AwayFromZero));

        drift.Should().BeLessThanOrEqualTo(
            tolerance,
            $"estimated {estimatedTokens} tokens, actual {actualTokens} tokens, tolerance {tolerance} tokens");
    }

    private static int EstimateDecisionResultTokens(DecisionResult decisionResult)
    {
        var total = 0;
        var clustered = ResultClusterer.Cluster([.. decisionResult.Decisions]);

        foreach (var item in clustered.Items)
        {
            switch (item)
            {
                case ClusterHeader:
                    total += ResultClusterer.ClusterHeaderTokenCost;
                    break;
                case RenderingDecision decision:
                    total += EstimateDecisionTokens(decision);
                    break;
            }
        }

        return total;
    }

    private static int EstimateDecisionTokens(RenderingDecision decision)
    {
        var total = decision.EstimatedTokens;
        if (decision.ChildDecisions is not { Count: > 0 })
            return total;

        foreach (var child in decision.ChildDecisions)
            total += EstimateDecisionTokens(child);

        return total;
    }

    private static List<ExploreResult> CreateRealisticExploreResults()
    {
        return
        [
            new ExploreResult(
                Uri: "file:///src/RepoQL.Explore/ExploreOrchestrator.cs",
                Confidence: 98,
                Kind: null,
                Headline: "exploreorchestrator.cs | ExploreOrchestrator | ExecuteAsync | 273 ln, ~2.3k tok",
                Structure: "SearchAsync()\\nDeduplicateResults()\\nOutputComposer.Compose()",
                Snippet: null,
                Lang: "csharp"),
            new ExploreResult(
                Uri: "file:///src/RepoQL.Explore/ValueBasedAllocator.cs",
                Confidence: 95,
                Kind: null,
                Headline: "valuebasedallocator.cs | ValueBasedAllocator | Allocate | 273 ln, ~2.4k tok",
                Structure: "Allocate()\\nAllocateWithinFile()\\nPickBestFit()",
                Snippet: null,
                Lang: "csharp"),
            new ExploreResult(
                Uri: "file:///src/RepoQL.Explore/RepresentationFormatter.cs",
                Confidence: 91,
                Kind: null,
                Headline: "representationformatter.cs | RepresentationFormatter | formatting for Minimal/Compact/Standard/Rich",
                Structure: "FormatMinimal()\\nFormatCompact()\\nFormatStandard()\\nFormatRich()",
                Snippet: null,
                Lang: "csharp"),
            new ExploreResult(
                Uri: "file:///src/RepoQL.Explore/OutputComposer.cs",
                Confidence: 87,
                Kind: null,
                Headline: "outputcomposer.cs | OutputComposer | Compose | 206 ln, ~1.6k tok",
                Structure: "Compose()\\nFormatWithChildren()\\nFormatClusterHeader()",
                Snippet: null,
                Lang: "csharp"),
            new ExploreResult(
                Uri: "file:///src/RepoQL.Explore/ExploreTokenEstimator.cs",
                Confidence: 84,
                Kind: null,
                Headline: "exploretokenestimator.cs | ExploreTokenEstimator | representation-level token estimates",
                Structure: "EstimateMinimal()\\nEstimateCompact()\\nEstimateStandard()\\nEstimateRich()",
                Snippet: null,
                Lang: "csharp"),
            new ExploreResult(
                Uri: "file:///src/RepoQL.Explore/Search/QueryStrategy.cs",
                Confidence: 81,
                Kind: null,
                Headline: "querystrategy.cs | QueryStrategy | breadth-driven query plan",
                Structure: "Plan()\\nObjectFetchMode\\nQueryPlan",
                Snippet: null,
                Lang: "csharp")
        ];
    }
}
