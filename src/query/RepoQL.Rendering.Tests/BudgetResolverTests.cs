using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class BudgetResolverTests
{
    private static readonly int[] BroadModerateConfidences = [70, 66, 62, 58, 54, 50, 46, 42, 38, 35];
    private static readonly int[] SpreadConfidences = [60, 58, 56, 54, 52];
    private static readonly int[] ConcentratedConfidences = [98, 10, 10, 10, 10];
    private static readonly int[] HighQualityConfidences = [95, 90, 85];
    private static readonly int[] LowerQualityConfidences = [40, 38, 36];

    // --- Explicit int budget: passthrough, no resolution ---

    [Test]
    [DisplayName("Explicit int budget passes through unchanged")]
    public void Given_ExplicitBudget_When_Resolving_Then_Passthrough()
    {
        var query = new ExploreQuery(TokenBudget: 3000, Keywords: "test");
        var results = new List<ExploreResult> { StructuredResult(95) };

        var resolution = BudgetResolver.Resolve(query, results, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().Be(3000);
        resolution.StatedCap.Should().BeNull();
    }

    [Test]
    [DisplayName("Explicit int budget is not reduced even for weak results")]
    public void Given_ExplicitBudgetWithWeakResults_When_Resolving_Then_StillPassthrough()
    {
        var query = new ExploreQuery(TokenBudget: 3000, Keywords: "test");
        var results = Enumerable.Range(0, 5)
            .Select(i => ResultBuilder.Document(confidence: 20, headlineLength: 60))
            .ToList();

        var resolution = BudgetResolver.Resolve(query, results, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().Be(3000);
    }

    // --- Tier mode: system resolves from results ---

    [Test]
    [DisplayName("Tier: precise match resolves to floor of range")]
    public void Given_TierMediumWithSingleStrongResult_When_Resolving_Then_UsesFloor()
    {
        var query = new ExploreQuery(TokenBudget: 0, BudgetTier: "medium", Keywords: "test");
        var results = new List<ExploreResult> { StructuredResult(95) };

        var resolution = BudgetResolver.Resolve(query, results, hasSearchCriteria: true);

        // medium range is 1500-3500; single result demand is low → clamps to 1500
        resolution.EffectiveBudget.Should().BeLessThanOrEqualTo(1500);
    }

    [Test]
    [DisplayName("Tier: broad moderate matches resolve within range")]
    public void Given_TierMediumWithBroadResults_When_Resolving_Then_UsesIntermediateBudget()
    {
        var query = new ExploreQuery(TokenBudget: 0, BudgetTier: "medium", Keywords: "test");
        var results = BroadModerateConfidences.Select(confidence => StructuredResult(confidence, childCount: 3)).ToList();

        var resolution = BudgetResolver.Resolve(query, results, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().BeGreaterThanOrEqualTo(1500);
        resolution.EffectiveBudget.Should().BeLessThanOrEqualTo(3500);
    }

    [Test]
    [DisplayName("Tier: weak results resolve to floor of range")]
    public void Given_TierHighWithWeakResults_When_Resolving_Then_UsesFloor()
    {
        var query = new ExploreQuery(TokenBudget: 0, BudgetTier: "high", Keywords: "test");
        var results = Enumerable.Range(0, 5)
            .Select(i => ResultBuilder.Document(confidence: 20, headlineLength: 60))
            .ToList();

        var resolution = BudgetResolver.Resolve(query, results, hasSearchCriteria: true);

        // high range floor is 3000
        resolution.EffectiveBudget.Should().Be(3000);
    }

    [Test]
    [DisplayName("Tier: zero results resolve to floor of range")]
    public void Given_TierLowWithNoResults_When_Resolving_Then_UsesFloor()
    {
        var query = new ExploreQuery(TokenBudget: 0, BudgetTier: "low", Keywords: "test");

        var resolution = BudgetResolver.Resolve(query, Array.Empty<ExploreResult>(), hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().Be(800);
        resolution.StatedCap.Should().Be(1500);
    }

    [Test]
    [DisplayName("Tier: inventory scan scales with result count")]
    public void Given_TierMediumInventoryScan_When_Resolving_Then_ScalesWithCount()
    {
        var querySmall = new ExploreQuery(TokenBudget: 0, BudgetTier: "medium");
        var queryLarge = new ExploreQuery(TokenBudget: 0, BudgetTier: "medium");
        var small = Enumerable.Range(0, 50).Select(i => ResultBuilder.Document(confidence: 10, headlineLength: 0)).ToList();
        var large = Enumerable.Range(0, 200).Select(i => ResultBuilder.Document(confidence: 10, headlineLength: 0)).ToList();

        var smallResolution = BudgetResolver.Resolve(querySmall, small, hasSearchCriteria: false);
        var largeResolution = BudgetResolver.Resolve(queryLarge, large, hasSearchCriteria: false);

        largeResolution.EffectiveBudget.Should().BeGreaterThan(smallResolution.EffectiveBudget);
    }

    [Test]
    [DisplayName("Tier: resolved budget never exceeds tier max")]
    public void Given_TierLowWithManyStrongResults_When_Resolving_Then_NeverExceedsMax()
    {
        var query = new ExploreQuery(TokenBudget: 0, BudgetTier: "low", Keywords: "test");
        var results = Enumerable.Range(0, 30).Select(i => StructuredResult(95, childCount: 5)).ToList();

        var resolution = BudgetResolver.Resolve(query, results, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().BeLessThanOrEqualTo(1500); // low max
    }

    [Test]
    [DisplayName("Tier: spread results resolve higher budget than concentrated")]
    public void Given_TierSpreadVsConcentrated_When_Resolving_Then_SpreadGetsMore()
    {
        // 15 results at similar confidence vs 15 results with one outlier + noise
        var spread = Enumerable.Range(0, 15)
            .Select(i => StructuredResult(70 - i * 2, childCount: 3)).ToList();
        var concentrated = new[] { 98 }.Concat(Enumerable.Repeat(15, 14))
            .Select(c => StructuredResult(c, childCount: 3)).ToList();

        var querySpread = new ExploreQuery(TokenBudget: 0, BudgetTier: "high", Keywords: "test");
        var queryConc = new ExploreQuery(TokenBudget: 0, BudgetTier: "high", Keywords: "test");

        var spreadRes = BudgetResolver.Resolve(querySpread, spread, hasSearchCriteria: true);
        var concRes = BudgetResolver.Resolve(queryConc, concentrated, hasSearchCriteria: true);

        spreadRes.EffectiveBudget.Should().BeGreaterThan(concRes.EffectiveBudget);
    }

    [Test]
    [DisplayName("Tier: higher quality resolves higher budget at same count")]
    public void Given_TierHighVsLowQuality_When_Resolving_Then_HighQualityGetsMore()
    {
        // 10 results — same count, different quality bands
        var highQuality = Enumerable.Range(0, 10)
            .Select(i => StructuredResult(90 - i * 2, childCount: 3)).ToList();
        var lowerQuality = Enumerable.Range(0, 10)
            .Select(i => StructuredResult(45 - i, childCount: 3)).ToList();

        var queryHigh = new ExploreQuery(TokenBudget: 0, BudgetTier: "high", Keywords: "test");
        var queryLow = new ExploreQuery(TokenBudget: 0, BudgetTier: "high", Keywords: "test");

        var highRes = BudgetResolver.Resolve(queryHigh, highQuality, hasSearchCriteria: true);
        var lowRes = BudgetResolver.Resolve(queryLow, lowerQuality, hasSearchCriteria: true);

        highRes.EffectiveBudget.Should().BeGreaterThan(lowRes.EffectiveBudget);
    }

    [Test]
    [DisplayName("Tier: footer shows resolved/max format")]
    public void Given_TierResolution_When_ComposingOutput_Then_FooterShowsBothValues()
    {
        var decision = new RenderingDecision(
            new ExploreResult("file:///src/Auth.cs", 85, null, "Auth service", null, null, null),
            Representation.Compact,
            10);
        var decisionResult = new DecisionResult([decision], 0, null);
        var status = new TrustSignal(
            IndexTotal: 10,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 50);
        var budgetResolution = new BudgetResolution(EffectiveBudget: 800, StatedCap: 3500);

        var footer = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 750, budgetResolution: budgetResolution);

        footer.Should().Contain("/3.5k tok");
    }

    [Test]
    [DisplayName("Explicit budget: footer shows simple token count")]
    public void Given_ExplicitResolution_When_ComposingOutput_Then_FooterShowsSimpleCount()
    {
        var status = new TrustSignal(
            IndexTotal: 10,
            IndexPending: 0,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: true,
            SemanticReady: true,
            SemanticPercent: 100,
            ExecutionTimeMs: 50);
        var budgetResolution = new BudgetResolution(EffectiveBudget: 3000, StatedCap: null);

        var footer = RepresentationFormatter.FormatStatusFooter(status, tokenCount: 2800, budgetResolution: budgetResolution);

        footer.Should().Contain("2.8k tok");
        footer.Should().NotContain("/");
    }

    // --- Tier range parsing ---

    [Test]
    [DisplayName("ParseTierRange returns correct ranges")]
    public void Given_TierNames_When_Parsing_Then_ReturnsExpectedRanges()
    {
        BudgetResolver.ParseTierRange("low").Should().Be((800, 1500));
        BudgetResolver.ParseTierRange("medium").Should().Be((1500, 3500));
        BudgetResolver.ParseTierRange("high").Should().Be((3000, 6000));
        BudgetResolver.ParseTierRange("MEDIUM").Should().Be((1500, 3500)); // case insensitive
        BudgetResolver.ParseTierRange("unknown").Should().Be((1500, 3500)); // defaults to medium
    }

    private static ExploreResult StructuredResult(int confidence, int childCount = 0)
    {
        IReadOnlyList<ExploreResult>? children = childCount == 0
            ? null
            : Enumerable.Range(0, childCount)
                .Select(i => new ExploreResult(
                    Uri: $"file:///test/result{confidence}.cs#symbol=Child{i}",
                    Confidence: Math.Max(confidence - i - 1, 1),
                    Kind: "method",
                    Headline: $"Child {i}",
                    Structure: null,
                    Snippet: null,
                    Lang: null))
                .ToList();

        return new ExploreResult(
            Uri: $"file:///test/result{confidence}.cs",
            Confidence: confidence,
            Kind: null,
            Headline: $"Result {confidence}",
            Structure: "Type\n  Method()",
            Snippet: null,
            Lang: "csharp",
            ChildObjects: children);
    }
}
