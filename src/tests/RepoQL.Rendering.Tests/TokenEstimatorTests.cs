using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Rendering.Tests.TestData;
using RepoQL.Explore;

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
    [Arguments("Hello", 1)]
    [Arguments("Hello, world!", 4)]
    [Arguments("The quick brown fox jumps over the lazy dog.", 10)]
    [DisplayName("Real text produces expected token counts")]
    public void Given_RealText_When_EstimateTokens_Then_ReturnsCorrectCount(string text, int expectedTokens)
    {
        TokenEstimator.EstimateTokens(text).Should().Be(expectedTokens);
    }

    [Test]
    [DisplayName("Longer text produces more tokens")]
    public void Given_TextOfVaryingLengths_When_EstimateTokens_Then_LongerProducesMore()
    {
        var short_ = "Hello";
        var medium = "Hello, this is a medium length sentence.";
        var long_ = "Hello, this is a much longer sentence that contains significantly more text and should produce many more tokens.";

        var shortTokens = TokenEstimator.EstimateTokens(short_);
        var mediumTokens = TokenEstimator.EstimateTokens(medium);
        var longTokens = TokenEstimator.EstimateTokens(long_);

        shortTokens.Should().BeLessThan(mediumTokens);
        mediumTokens.Should().BeLessThan(longTokens);
    }

    [Test]
    [DisplayName("Minimal representation estimates headline plus overhead")]
    public void Given_Result_When_EstimateMinimal_Then_IncludesHeadlineAndOverhead()
    {
        var smallHeadline = ResultBuilder.Create(80, headlineLength: 20);
        var largeHeadline = ResultBuilder.Create(80, headlineLength: 100);

        var smallTokens = ExploreTokenEstimator.EstimateMinimal(smallHeadline);
        var largeTokens = ExploreTokenEstimator.EstimateMinimal(largeHeadline);

        // Both should produce positive token counts
        smallTokens.Should().BeGreaterThan(0);
        largeTokens.Should().BeGreaterThan(smallTokens, "larger headline produces more tokens");
    }

    [Test]
    [DisplayName("Compact representation estimates uri, headline, and formatting")]
    public void Given_Document_When_EstimateCompact_Then_IncludesUriAndHeadline()
    {
        var result = ResultBuilder.Document(80, headlineLength: 40);

        var tokens = ExploreTokenEstimator.EstimateCompact(result);

        // Should produce reasonable token count for URI + headline + formatting
        tokens.Should().BeGreaterThan(5);
    }

    [Test]
    [DisplayName("Compact representation includes kind badge for objects")]
    public void Given_Object_When_EstimateCompact_Then_IncludesKindBadge()
    {
        var doc = ResultBuilder.Document(80, headlineLength: 40);
        var obj = ResultBuilder.Create(80, headlineLength: 40, kind: "method");

        var docTokens = ExploreTokenEstimator.EstimateCompact(doc);
        var objTokens = ExploreTokenEstimator.EstimateCompact(obj);

        objTokens.Should().BeGreaterThan(docTokens, "object includes [kind] badge");
    }

    [Test]
    [DisplayName("Standard representation includes structure")]
    public void Given_ResultWithStructure_When_EstimateStandard_Then_IncludesStructure()
    {
        var withoutStructure = ResultBuilder.Document(80, headlineLength: 40);
        var withStructure = ResultBuilder.Create(80, headlineLength: 40, structureLength: 200);

        var tokensWithout = ExploreTokenEstimator.EstimateStandard(withoutStructure);
        var tokensWith = ExploreTokenEstimator.EstimateStandard(withStructure);

        tokensWith.Should().BeGreaterThan(tokensWithout, "structure adds tokens");
    }

    [Test]
    [DisplayName("Rich representation estimates snippet")]
    public void Given_ObjectWithSnippet_When_EstimateRich_Then_IncludesSnippet()
    {
        var smallSnippet = ResultBuilder.ObjectResult(90, snippetLength: 100);
        var largeSnippet = ResultBuilder.ObjectResult(90, snippetLength: 500);

        var smallTokens = ExploreTokenEstimator.EstimateRich(smallSnippet);
        var largeTokens = ExploreTokenEstimator.EstimateRich(largeSnippet);

        // Larger snippets should produce more tokens
        largeTokens.Should().BeGreaterThan(smallTokens, "larger snippet produces more tokens");
    }

    [Test]
    [DisplayName("Estimate dispatches to correct method based on level")]
    public void Given_RepresentationLevel_When_Estimate_Then_DispatchesCorrectly()
    {
        var result = ResultBuilder.Create(80, headlineLength: 40, structureLength: 100, snippetLength: 200);

        var compact = ExploreTokenEstimator.Estimate(result, Representation.Compact);
        var standard = ExploreTokenEstimator.Estimate(result, Representation.Standard);
        var rich = ExploreTokenEstimator.Estimate(result, Representation.Rich);

        compact.Should().Be(ExploreTokenEstimator.EstimateCompact(result));
        standard.Should().Be(ExploreTokenEstimator.EstimateStandard(result));
        rich.Should().Be(ExploreTokenEstimator.EstimateRich(result));
    }

    [Test]
    [DisplayName("Representations increase in token cost: Minimal < Compact < Standard")]
    public void Given_ResultWithAllFields_When_EstimateAllLevels_Then_CostIncreases()
    {
        var result = ResultBuilder.Create(80, headlineLength: 50, structureLength: 200, snippetLength: 400);

        var minimal = ExploreTokenEstimator.EstimateMinimal(result);
        var compact = ExploreTokenEstimator.EstimateCompact(result);
        var standard = ExploreTokenEstimator.EstimateStandard(result);

        minimal.Should().BeLessThan(compact, "Minimal has no URI");
        compact.Should().BeLessThan(standard, "Compact has no structure");
    }

    [Test]
    [DisplayName("Truncation summary with confidence estimates more tokens")]
    public void Given_TruncationSummary_When_HasConfidence_Then_EstimatesMore()
    {
        var withConf = ExploreTokenEstimator.EstimateTruncationSummary(hasConfidence: true);
        var withoutConf = ExploreTokenEstimator.EstimateTruncationSummary(hasConfidence: false);

        withConf.Should().BeGreaterThan(withoutConf);
    }

    // Note: Child objects are now estimated separately via ValueBasedAllocator
    // rather than recursively within ExploreTokenEstimator. This enables per-child
    // representation level allocation based on value/confidence.
}
