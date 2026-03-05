using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class ValueBasedAllocatorTests
{
    [Test]
    [DisplayName("breadth=5 uses identity exponent (1.0)")]
    public void Given_BreadthFive_When_GettingModifier_Then_ReturnsIdentity()
    {
        ValueBasedAllocator.GetBreadthModifier(5).Should().Be(1.0);
    }

    [Test]
    [DisplayName("breadth=1 concentrates budget on high-confidence results more than breadth=5")]
    public void Given_LowBreadth_When_Allocating_Then_TopResultGetsMoreBudget()
    {
        var confidences = new[] { 95.0, 70.0, 45.0, 20.0 };

        var lowShares = ComputeProportionalShares(confidences, breadth: 1);
        var defaultShares = ComputeProportionalShares(confidences, breadth: 5);

        lowShares[0].Should().BeGreaterThan(defaultShares[0]);
        lowShares[^1].Should().BeLessThan(defaultShares[^1]);
    }

    [Test]
    [DisplayName("breadth=10 flattens budget distribution compared to breadth=5")]
    public void Given_HighBreadth_When_Allocating_Then_DistributionFlattens()
    {
        var confidences = new[] { 95.0, 70.0, 45.0, 20.0 };

        var highShares = ComputeProportionalShares(confidences, breadth: 10);
        var defaultShares = ComputeProportionalShares(confidences, breadth: 5);

        highShares[0].Should().BeLessThan(defaultShares[0]);
        highShares[^1].Should().BeGreaterThan(defaultShares[^1]);
    }

    [Test]
    [DisplayName("Balanced breadth with 2 results allocates more tokens per result than with 20 results")]
    public void Given_BalancedBreadth_When_FewResults_Then_TokensPerResultIncrease()
    {
        const int tokenBudget = 2000;

        var twoResults = CreateResults(2);
        var twentyResults = CreateResults(20);

        var twoResultDecisions = ValueBasedAllocator.Allocate(twoResults, tokenBudget, 5);
        var twentyResultDecisions = ValueBasedAllocator.Allocate(twentyResults, tokenBudget, 5);

        var twoResultAverageTokens = twoResultDecisions.Average(TotalTokensPerDecision);
        var twentyResultAverageTokens = twentyResultDecisions.Average(TotalTokensPerDecision);

        twoResultAverageTokens.Should().BeGreaterThan(twentyResultAverageTokens);
    }

    private static List<ExploreResult> CreateResults(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new ExploreResult(
                Uri: $"file:///test/result{i}.cs",
                Confidence: 100 - i,
                Kind: null,
                Headline: new string('h', 80),
                Structure: null,
                Snippet: null,
                Lang: null,
                ChildObjects: Enumerable.Range(0, 12)
                    .Select(j => ResultBuilder.Create(
                        confidence: 90 - j,
                        headlineLength: 60,
                        snippetLength: 300,
                        kind: "method",
                        uri: $"file:///test/result{i}.cs#symbol=Method{j}"))
                    .ToList()))
            .ToList();
    }

    private static double[] ComputeProportionalShares(double[] confidences, int breadth)
    {
        var exponent = ValueBasedAllocator.GetBreadthModifier(breadth);
        var evs = confidences.Select(c => Math.Pow(c, exponent)).ToArray();
        var total = evs.Sum();
        return evs.Select(v => v / total).ToArray();
    }

    private static int TotalTokensPerDecision(RenderingDecision decision)
    {
        var childTokens = decision.ChildDecisions?.Sum(TotalTokensPerDecision) ?? 0;
        return decision.EstimatedTokens + childTokens;
    }
}
