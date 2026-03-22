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
    [DisplayName("Minimal representation estimates uri plus overhead")]
    public void Given_Result_When_EstimateMinimal_Then_EstimatesUriTokens()
    {
        var result = ResultBuilder.Create(80, headlineLength: 20);

        var tokens = ExploreTokenEstimator.EstimateMinimal(result);

        tokens.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("Minimal representation is uri tokens plus one")]
    public void Given_Result_When_EstimateMinimal_Then_IsUriTokensPlusOne()
    {
        var result = ResultBuilder.Document(80, headlineLength: 40);

        var tokens = ExploreTokenEstimator.EstimateMinimal(result);
        var expected = TokenEstimator.EstimateTokens(result.Uri) + 1;

        tokens.Should().Be(expected);
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
    [DisplayName("Compact representation ignores kind because kind badge is not rendered")]
    public void Given_ObjectKind_When_EstimateCompact_Then_DoesNotChangeCost()
    {
        var doc = ResultBuilder.Document(80, headlineLength: 40);
        var obj = doc with { Kind = "method" };

        var docTokens = ExploreTokenEstimator.EstimateCompact(doc);
        var objTokens = ExploreTokenEstimator.EstimateCompact(obj);

        objTokens.Should().Be(docTokens, "kind badge is not rendered");
    }

    [Test]
    [DisplayName("Confidence token cost is only charged when confidence is shown")]
    public void Given_ShowConfidenceFlag_When_Estimating_Then_ConfidenceCostIsConditional()
    {
        var result = ResultBuilder.Create(80, headlineLength: 40, structureLength: 120, snippetLength: 160);

        var compactWithout = ExploreTokenEstimator.EstimateCompact(result, showConfidence: false);
        var compactWith = ExploreTokenEstimator.EstimateCompact(result, showConfidence: true);
        var standardWithout = ExploreTokenEstimator.EstimateStandard(result, showConfidence: false);
        var standardWith = ExploreTokenEstimator.EstimateStandard(result, showConfidence: true);
        var richWithout = ExploreTokenEstimator.EstimateRich(result, showConfidence: false);
        var richWith = ExploreTokenEstimator.EstimateRich(result, showConfidence: true);

        compactWith.Should().Be(compactWithout + 2);
        standardWith.Should().Be(standardWithout + 2);
        richWith.Should().Be(richWithout + 2);
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
    [DisplayName("Standard representation adds provenance token allowance")]
    public void Given_StandardWithProvenance_When_EstimateStandard_Then_AddsTwelveTokens()
    {
        var withoutProvenance = ResultBuilder.Document(80, headlineLength: 40, structureLength: 120);
        var withProvenance = withoutProvenance with { Provenance = "semantic" };

        var withoutTokens = ExploreTokenEstimator.EstimateStandard(withoutProvenance);
        var withTokens = ExploreTokenEstimator.EstimateStandard(withProvenance);

        withTokens.Should().Be(withoutTokens + 12);
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
    [DisplayName("Rich representation adds provenance token allowance")]
    public void Given_RichWithProvenance_When_EstimateRich_Then_AddsTwelveTokens()
    {
        var withoutProvenance = ResultBuilder.ObjectResult(90, snippetLength: 120);
        var withProvenance = withoutProvenance with { Provenance = "content" };

        var withoutTokens = ExploreTokenEstimator.EstimateRich(withoutProvenance);
        var withTokens = ExploreTokenEstimator.EstimateRich(withProvenance);

        withTokens.Should().Be(withoutTokens + 12);
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
    [DisplayName("Estimate dispatch honors showConfidence flag")]
    public void Given_ShowConfidenceFlag_When_UsingEstimateDispatch_Then_UsesConditionalConfidenceCost()
    {
        var result = ResultBuilder.Create(80, headlineLength: 40, structureLength: 100, snippetLength: 200);

        var compactWithout = ExploreTokenEstimator.Estimate(result, Representation.Compact, showConfidence: false);
        var compactWith = ExploreTokenEstimator.Estimate(result, Representation.Compact, showConfidence: true);
        var standardWithout = ExploreTokenEstimator.Estimate(result, Representation.Standard, showConfidence: false);
        var standardWith = ExploreTokenEstimator.Estimate(result, Representation.Standard, showConfidence: true);
        var richWithout = ExploreTokenEstimator.Estimate(result, Representation.Rich, showConfidence: false);
        var richWith = ExploreTokenEstimator.Estimate(result, Representation.Rich, showConfidence: true);

        compactWith.Should().Be(compactWithout + 2);
        standardWith.Should().Be(standardWithout + 2);
        richWith.Should().Be(richWithout + 2);
    }

    [Test]
    [DisplayName("Standard representation remains more expensive than Minimal and Compact")]
    public void Given_ResultWithAllFields_When_EstimateAllLevels_Then_StandardIsMostExpensive()
    {
        var result = ResultBuilder.Create(80, headlineLength: 50, structureLength: 200, snippetLength: 400);

        var minimal = ExploreTokenEstimator.EstimateMinimal(result);
        var compact = ExploreTokenEstimator.EstimateCompact(result);
        var standard = ExploreTokenEstimator.EstimateStandard(result);

        minimal.Should().BeGreaterThan(0);
        compact.Should().BeGreaterThan(0);
        compact.Should().BeLessThan(standard, "Compact has no structure");
        minimal.Should().BeLessThan(standard, "Minimal has no structure");
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
