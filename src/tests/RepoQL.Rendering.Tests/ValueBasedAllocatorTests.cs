using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class ValueBasedAllocatorTests
{
    [Test]
    [DisplayName("Locate with 2 results allocates more tokens per result than Locate with 20 results")]
    public void Given_LocateIntent_When_FewResults_Then_TokensPerResultIncrease()
    {
        const int tokenBudget = 2000;

        var twoResults = CreateResults(2);
        var twentyResults = CreateResults(20);

        var twoResultDecisions = ValueBasedAllocator.Allocate(twoResults, tokenBudget, Intent.Locate);
        var twentyResultDecisions = ValueBasedAllocator.Allocate(twentyResults, tokenBudget, Intent.Locate);

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

    private static int TotalTokensPerDecision(RenderingDecision decision)
    {
        var childTokens = decision.ChildDecisions?.Sum(TotalTokensPerDecision) ?? 0;
        return decision.EstimatedTokens + childTokens;
    }
}
