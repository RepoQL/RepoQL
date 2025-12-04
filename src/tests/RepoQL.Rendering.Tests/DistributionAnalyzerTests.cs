using AwesomeAssertions;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class DistributionAnalyzerTests
{
    [Test]
    [DisplayName("Empty results returns empty analysis with Even shape")]
    public void Given_EmptyResults_When_Analyze_Then_ReturnsEmptyEven()
    {
        var results = Array.Empty<XrayResult>();

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.TopTier.Should().BeEmpty();
        analysis.MiddleTier.Should().BeEmpty();
        analysis.BottomTier.Should().BeEmpty();
        analysis.Shape.Should().Be(DistributionShape.Even);
        analysis.TotalCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Single result goes to top tier")]
    public void Given_SingleResult_When_Analyze_Then_GoesToTopTier()
    {
        var results = new[] { ResultBuilder.Create(50) };

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.TopTier.Should().HaveCount(1);
        analysis.MiddleTier.Should().BeEmpty();
        analysis.BottomTier.Should().BeEmpty();
        analysis.Shape.Should().Be(DistributionShape.Even);
    }

    [Test]
    [DisplayName("Results >= 80% always go to top tier")]
    public void Given_HighConfidenceResults_When_Analyze_Then_GoToTopTier()
    {
        var results = new[]
        {
            ResultBuilder.Create(95),
            ResultBuilder.Create(85),
            ResultBuilder.Create(80),
            ResultBuilder.Create(50),
            ResultBuilder.Create(40),
        };

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.TopTier.Should().HaveCount(3, "80%+ goes to top tier");
        analysis.TopTier.Select(r => r.Confidence).Should().BeEquivalentTo([95, 85, 80]);
    }

    [Test]
    [DisplayName("Results < 50% go to bottom tier when also below 25th percentile")]
    public void Given_LowConfidenceResults_When_Analyze_Then_GoToBottomTier()
    {
        var results = new[]
        {
            ResultBuilder.Create(90),
            ResultBuilder.Create(85),
            ResultBuilder.Create(80),
            ResultBuilder.Create(75),
            ResultBuilder.Create(70),
            ResultBuilder.Create(40),
            ResultBuilder.Create(30),
        };

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.BottomTier.Should().HaveCount(2, "40% and 30% are below 50% and below 25th percentile");
    }

    [Test]
    [DisplayName("Lumpy distribution: few strong matches, many weak")]
    public void Given_FewStrongManyWeak_When_Analyze_Then_DetectsLumpy()
    {
        var results = new[]
        {
            ResultBuilder.Create(95),
            ResultBuilder.Create(94),
            ResultBuilder.Create(40),
            ResultBuilder.Create(38),
            ResultBuilder.Create(35),
            ResultBuilder.Create(33),
            ResultBuilder.Create(30),
        };

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.Shape.Should().Be(DistributionShape.Lumpy);
        analysis.TopTier.Should().HaveCount(2, "only the 95% and 94% are top tier");
    }

    [Test]
    [DisplayName("Even distribution: all scores within 20% range")]
    public void Given_ClusteredScores_When_Analyze_Then_DetectsEven()
    {
        var results = new[]
        {
            ResultBuilder.Create(70),
            ResultBuilder.Create(68),
            ResultBuilder.Create(65),
            ResultBuilder.Create(62),
            ResultBuilder.Create(60),
            ResultBuilder.Create(55),
        };

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.Shape.Should().Be(DistributionShape.Even);
    }

    [Test]
    [DisplayName("All same score is Even distribution")]
    public void Given_AllSameScore_When_Analyze_Then_DetectsEven()
    {
        var results = Enumerable.Range(0, 10).Select(_ => ResultBuilder.Create(60)).ToArray();

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.Shape.Should().Be(DistributionShape.Even);
    }

    [Test]
    [DisplayName("AllResults returns items in tier order")]
    public void Given_MixedResults_When_AllResults_Then_ReturnsInTierOrder()
    {
        var results = new[]
        {
            ResultBuilder.Create(95),
            ResultBuilder.Create(60),
            ResultBuilder.Create(30),
        };

        var analysis = DistributionAnalyzer.Analyze(results);
        var allResults = analysis.AllResults.ToList();

        // Should be Top, then Middle, then Bottom
        allResults[0].Confidence.Should().Be(95, "top tier first");
        allResults[^1].Confidence.Should().Be(30, "bottom tier last");
    }

    [Test]
    [DisplayName("TotalCount matches input count")]
    public void Given_Results_When_TotalCount_Then_MatchesInputCount()
    {
        var results = Enumerable.Range(1, 25).Select(i => ResultBuilder.Create(i * 4)).ToArray();

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.TotalCount.Should().Be(25);
    }

    [Test]
    [DisplayName("Tiers are sorted by confidence descending within each tier")]
    public void Given_MixedResults_When_Analyze_Then_TiersSortedDescending()
    {
        var results = new[]
        {
            ResultBuilder.Create(82),
            ResultBuilder.Create(95),
            ResultBuilder.Create(88),
            ResultBuilder.Create(60),
            ResultBuilder.Create(55),
        };

        var analysis = DistributionAnalyzer.Analyze(results);

        analysis.TopTier.Select(r => r.Confidence).Should().BeInDescendingOrder();
        analysis.MiddleTier.Select(r => r.Confidence).Should().BeInDescendingOrder();
    }
}
