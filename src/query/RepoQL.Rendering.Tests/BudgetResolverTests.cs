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

    [Test]
    [DisplayName("Precise match resolves to floor budget")]
    public void Given_SingleStrongResult_When_Resolving_Then_UsesFloor()
    {
        var results = new List<ExploreResult> { StructuredResult(95) };

        var resolution = BudgetResolver.Resolve(results, statedCap: 3000, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().Be(800);
        resolution.StatedCap.Should().Be(3000);
    }

    [Test]
    [DisplayName("Broad moderate matches resolve between floor and cap")]
    public void Given_BroadModerateResults_When_Resolving_Then_UsesIntermediateBudget()
    {
        var results = BroadModerateConfidences.Select(confidence => StructuredResult(confidence, childCount: 3)).ToList();

        var resolution = BudgetResolver.Resolve(results, statedCap: 3000, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().BeGreaterThan(800);
        resolution.EffectiveBudget.Should().BeLessThan(3000);
    }

    [Test]
    [DisplayName("Weak results fall back to floor budget")]
    public void Given_WeakResults_When_Resolving_Then_UsesFloor()
    {
        var results = Enumerable.Range(0, 5)
            .Select(i => ResultBuilder.Document(confidence: 20, headlineLength: 60))
            .ToList();

        var resolution = BudgetResolver.Resolve(results, statedCap: 3000, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().Be(800);
    }

    [Test]
    [DisplayName("Small inventory scan resolves to floor budget")]
    public void Given_SmallInventoryScan_When_Resolving_Then_UsesFloor()
    {
        var results = Enumerable.Range(0, 50)
            .Select(i => ResultBuilder.Document(confidence: 10, headlineLength: 0))
            .ToList();

        var resolution = BudgetResolver.Resolve(results, statedCap: 3000, hasSearchCriteria: false);

        resolution.EffectiveBudget.Should().Be(800);
    }

    [Test]
    [DisplayName("Large inventory scan resolves to the stated cap")]
    public void Given_LargeInventoryScan_When_Resolving_Then_UsesCap()
    {
        var results = Enumerable.Range(0, 200)
            .Select(i => ResultBuilder.Document(confidence: 10, headlineLength: 0))
            .ToList();

        var resolution = BudgetResolver.Resolve(results, statedCap: 3000, hasSearchCriteria: false);

        resolution.EffectiveBudget.Should().Be(3000);
    }

    [Test]
    [DisplayName("Zero results pass through the stated cap")]
    public void Given_NoResults_When_Resolving_Then_Passthrough()
    {
        var resolution = BudgetResolver.Resolve(Array.Empty<ExploreResult>(), statedCap: 3000, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().Be(3000);
        resolution.StatedCap.Should().Be(3000);
    }

    [Test]
    [DisplayName("Resolved budget never exceeds the stated cap")]
    public void Given_RichHighConfidenceResults_When_Resolving_Then_NeverExceedsCap()
    {
        var results = Enumerable.Range(0, 30)
            .Select(i => StructuredResult(95, childCount: 5))
            .ToList();

        var resolution = BudgetResolver.Resolve(results, statedCap: 2000, hasSearchCriteria: true);

        resolution.EffectiveBudget.Should().BeLessThanOrEqualTo(2000);
    }

    [Test]
    [DisplayName("Distributed good matches resolve higher budget than one dominant match")]
    public void Given_SpreadVersusConcentratedScores_When_Resolving_Then_SpreadGetsMoreBudget()
    {
        var spread = SpreadConfidences.Select(confidence => StructuredResult(confidence, childCount: 3)).ToList();
        var concentrated = ConcentratedConfidences.Select(confidence => StructuredResult(confidence, childCount: 3)).ToList();

        var spreadResolution = BudgetResolver.Resolve(spread, statedCap: 3000, hasSearchCriteria: true);
        var concentratedResolution = BudgetResolver.Resolve(concentrated, statedCap: 3000, hasSearchCriteria: true);

        spreadResolution.EffectiveBudget.Should().BeGreaterThan(concentratedResolution.EffectiveBudget);
    }

    [Test]
    [DisplayName("Higher quality results resolve higher budget at the same count")]
    public void Given_HigherQualityMatches_When_Resolving_Then_UsesMoreBudget()
    {
        var highQuality = HighQualityConfidences.Select(confidence => StructuredResult(confidence, childCount: 5)).ToList();
        var lowerQuality = LowerQualityConfidences.Select(confidence => StructuredResult(confidence, childCount: 5)).ToList();

        var highResolution = BudgetResolver.Resolve(highQuality, statedCap: 3000, hasSearchCriteria: true);
        var lowResolution = BudgetResolver.Resolve(lowerQuality, statedCap: 3000, hasSearchCriteria: true);

        highResolution.EffectiveBudget.Should().BeGreaterThan(lowResolution.EffectiveBudget);
    }

    [Test]
    [DisplayName("Reduced budget footer shows used tokens against the stated cap")]
    public void Given_ReducedBudget_When_ComposingOutput_Then_FooterShowsUsedTokensAndCap()
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
        var budgetResolution = new BudgetResolution(EffectiveBudget: 800, StatedCap: 3000);

        var body = OutputComposer.Compose(decisionResult, showConfidence: true);
        var expectedTokens = TokenEstimator.EstimateTokens($"{body}\n");
        var expectedFooter = RepresentationFormatter.FormatStatusFooter(status, expectedTokens, budgetResolution: budgetResolution);
        var output = OutputComposer.Compose(decisionResult, showConfidence: true, status, budgetResolution);

        expectedFooter.Should().Contain("/3.0k tok");
        output.Should().EndWith(expectedFooter);
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
