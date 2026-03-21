using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class ValueBasedAllocatorTests
{
    [Test]
    [DisplayName("breadth=1 produces steep sigmoid (high k)")]
    public void Given_BreadthOne_When_GettingSigmoidK_Then_ReturnsSteepValue()
    {
        ValueBasedAllocator.GetSigmoidK(1).Should().BeGreaterThan(10.0);
    }

    [Test]
    [DisplayName("breadth=10 produces gentle sigmoid (low k)")]
    public void Given_BreadthTen_When_GettingSigmoidK_Then_ReturnsGentleValue()
    {
        ValueBasedAllocator.GetSigmoidK(10).Should().BeLessThan(5.0);
    }

    [Test]
    [DisplayName("breadth=1 concentrates budget on high-confidence results more than breadth=5")]
    public void Given_LowBreadth_When_Allocating_Then_TopResultGetsMoreBudget()
    {
        const int tokenBudget = 2000;
        var results = CreateResults(4);

        var lowBreadth = ValueBasedAllocator.Allocate(results, tokenBudget, 1);
        var defaultBreadth = ValueBasedAllocator.Allocate(results, tokenBudget, 5);

        var lowTop = TotalTokensPerDecision(lowBreadth[0]);
        var defaultTop = TotalTokensPerDecision(defaultBreadth[0]);

        lowTop.Should().BeGreaterThan(defaultTop);
    }

    [Test]
    [DisplayName("breadth=10 flattens budget distribution compared to breadth=5")]
    public void Given_HighBreadth_When_Allocating_Then_DistributionFlattens()
    {
        const int tokenBudget = 2000;
        var results = CreateResults(4);

        var highBreadth = ValueBasedAllocator.Allocate(results, tokenBudget, 10);
        var defaultBreadth = ValueBasedAllocator.Allocate(results, tokenBudget, 5);

        var highTop = TotalTokensPerDecision(highBreadth[0]);
        var defaultTop = TotalTokensPerDecision(defaultBreadth[0]);

        highTop.Should().BeLessThan(defaultTop);
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

    [Test]
    [DisplayName("sigmoid k decreases monotonically as breadth increases")]
    public void Given_IncreasingBreadth_When_GettingSigmoidK_Then_KDecreases()
    {
        var previous = double.MaxValue;
        for (var breadth = 1; breadth <= 10; breadth++)
        {
            var k = ValueBasedAllocator.GetSigmoidK(breadth);
            k.Should().BeLessThan(previous);
            previous = k;
        }
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

    private static int TotalTokensPerDecision(RenderingDecision decision)
    {
        var childTokens = decision.ChildDecisions?.Sum(TotalTokensPerDecision) ?? 0;
        return decision.EstimatedTokens + childTokens;
    }
}
