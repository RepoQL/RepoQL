using AwesomeAssertions;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class TokenEstimatorTests
{
    [Test]
    [DisplayName("Null string returns 0 tokens")]
    public void Given_NullString_When_EstimateTokens_Then_ReturnsZero()
    {
        TokenEstimator.EstimateTokens(null).Should().Be(0);
    }

    [Test]
    [DisplayName("Empty string returns 0 tokens")]
    public void Given_EmptyString_When_EstimateTokens_Then_ReturnsZero()
    {
        TokenEstimator.EstimateTokens("").Should().Be(0);
    }

    [Test]
    [Arguments(4, 1)]
    [Arguments(8, 2)]
    [Arguments(100, 25)]
    [Arguments(400, 100)]
    [DisplayName("String length divided by 4 (rounded up) gives token count")]
    public void Given_StringOfLength_When_EstimateTokens_Then_ReturnsDividedByFour(int length, int expectedTokens)
    {
        var text = new string('x', length);
        TokenEstimator.EstimateTokens(text).Should().Be(expectedTokens);
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 1)]
    [Arguments(3, 1)]
    [Arguments(5, 2)]
    [Arguments(6, 2)]
    [Arguments(7, 2)]
    [DisplayName("Rounding up works correctly for non-multiples of 4")]
    public void Given_NonMultipleOf4_When_EstimateTokens_Then_RoundsUp(int length, int expectedTokens)
    {
        var text = new string('x', length);
        TokenEstimator.EstimateTokens(text).Should().Be(expectedTokens);
    }

    [Test]
    [DisplayName("Minimal representation estimates headline plus overhead")]
    public void Given_Result_When_EstimateMinimal_Then_IncludesHeadlineAndOverhead()
    {
        var result = ResultBuilder.Create(80, headlineLength: 40);

        var tokens = TokenEstimator.EstimateMinimal(result);

        // headline (40/4=10) + overhead (1) = 11
        tokens.Should().Be(11);
    }

    [Test]
    [DisplayName("Compact representation estimates uri, headline, and formatting")]
    public void Given_Document_When_EstimateCompact_Then_IncludesUriAndHeadline()
    {
        var result = ResultBuilder.Document(80, headlineLength: 40);

        var tokens = TokenEstimator.EstimateCompact(result);

        // confidence (2) + uri (~10) + newline (1) + headline (10) + overhead (2) = ~25
        tokens.Should().BeGreaterThan(20);
        tokens.Should().BeLessThan(40);
    }

    [Test]
    [DisplayName("Compact representation includes kind badge for objects")]
    public void Given_Object_When_EstimateCompact_Then_IncludesKindBadge()
    {
        var doc = ResultBuilder.Document(80, headlineLength: 40);
        var obj = ResultBuilder.Create(80, headlineLength: 40, kind: "method");

        var docTokens = TokenEstimator.EstimateCompact(doc);
        var objTokens = TokenEstimator.EstimateCompact(obj);

        objTokens.Should().BeGreaterThan(docTokens, "object includes [kind] badge");
    }

    [Test]
    [DisplayName("Standard representation includes structure")]
    public void Given_ResultWithStructure_When_EstimateStandard_Then_IncludesStructure()
    {
        var withoutStructure = ResultBuilder.Document(80, headlineLength: 40);
        var withStructure = ResultBuilder.Create(80, headlineLength: 40, structureLength: 200);

        var tokensWithout = TokenEstimator.EstimateStandard(withoutStructure);
        var tokensWith = TokenEstimator.EstimateStandard(withStructure);

        tokensWith.Should().BeGreaterThan(tokensWithout + 40, "200 chars structure adds ~50 tokens");
    }

    [Test]
    [DisplayName("Rich representation estimates snippet without headline")]
    public void Given_ObjectWithSnippet_When_EstimateRich_Then_IncludesSnippet()
    {
        var result = ResultBuilder.ObjectResult(90, snippetLength: 400);

        var tokens = TokenEstimator.EstimateRich(result);

        // Should include snippet (400/4=100) plus formatting
        tokens.Should().BeGreaterThan(100);
        tokens.Should().BeLessThan(150);
    }

    [Test]
    [DisplayName("Estimate dispatches to correct method based on level")]
    public void Given_RepresentationLevel_When_Estimate_Then_DispatchesCorrectly()
    {
        var result = ResultBuilder.Create(80, headlineLength: 40, structureLength: 100, snippetLength: 200);

        var compact = TokenEstimator.Estimate(result, Representation.Compact);
        var standard = TokenEstimator.Estimate(result, Representation.Standard);
        var rich = TokenEstimator.Estimate(result, Representation.Rich);

        compact.Should().Be(TokenEstimator.EstimateCompact(result));
        standard.Should().Be(TokenEstimator.EstimateStandard(result));
        rich.Should().Be(TokenEstimator.EstimateRich(result));
    }

    [Test]
    [DisplayName("Representations increase in token cost: Minimal < Compact < Standard")]
    public void Given_ResultWithAllFields_When_EstimateAllLevels_Then_CostIncreases()
    {
        var result = ResultBuilder.Create(80, headlineLength: 50, structureLength: 200, snippetLength: 400);

        var minimal = TokenEstimator.EstimateMinimal(result);
        var compact = TokenEstimator.EstimateCompact(result);
        var standard = TokenEstimator.EstimateStandard(result);

        minimal.Should().BeLessThan(compact, "Minimal has no URI");
        compact.Should().BeLessThan(standard, "Compact has no structure");
    }

    [Test]
    [DisplayName("Truncation summary with confidence estimates more tokens")]
    public void Given_TruncationSummary_When_HasConfidence_Then_EstimatesMore()
    {
        var withConf = TokenEstimator.EstimateTruncationSummary(hasConfidence: true);
        var withoutConf = TokenEstimator.EstimateTruncationSummary(hasConfidence: false);

        withConf.Should().BeGreaterThan(withoutConf);
    }
}
