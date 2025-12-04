using AwesomeAssertions;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class LimitCalculatorTests
{
    [Test]
    [DisplayName("Zero results returns zero limit")]
    public void Given_ZeroResults_When_Calculate_Then_ReturnsZero()
    {
        var distribution = DistributionAnalyzer.Analyze(Array.Empty<XrayResult>());

        var limit = LimitCalculator.Calculate(distribution, Intent.Find, 1000, 0);

        limit.Should().Be(0);
    }

    [Test]
    [DisplayName("Lumpy distribution focuses on top tier plus context")]
    public void Given_LumpyDistribution_When_Calculate_Then_FocusesOnTopTier()
    {
        // 2 top tier, 10 middle tier, 5 bottom tier
        var results = new[]
        {
            ResultBuilder.Create(95),
            ResultBuilder.Create(90),
            ResultBuilder.Create(65),
            ResultBuilder.Create(64),
            ResultBuilder.Create(63),
            ResultBuilder.Create(62),
            ResultBuilder.Create(61),
            ResultBuilder.Create(60),
            ResultBuilder.Create(59),
            ResultBuilder.Create(58),
            ResultBuilder.Create(57),
            ResultBuilder.Create(56),
            ResultBuilder.Create(30),
            ResultBuilder.Create(28),
            ResultBuilder.Create(25),
            ResultBuilder.Create(22),
            ResultBuilder.Create(20),
        };
        var distribution = DistributionAnalyzer.Analyze(results);

        var limit = LimitCalculator.Calculate(distribution, Intent.Find, 2000, results.Length);

        // Top tier (2) + min(middle tier, 5) = 7
        limit.Should().BeLessThanOrEqualTo(10, "focuses on top + limited middle tier");
        limit.Should().BeGreaterThanOrEqualTo(2, "at least includes top tier");
    }

    [Test]
    [DisplayName("Even distribution maximizes coverage")]
    public void Given_EvenDistribution_When_Calculate_Then_MaximizesCoverage()
    {
        var results = Enumerable.Range(0, 20).Select(i => ResultBuilder.Create(60 + i % 10)).ToArray();
        var distribution = DistributionAnalyzer.Analyze(results);

        var limit = LimitCalculator.Calculate(distribution, Intent.Find, 800, results.Length);

        // 800 tokens / ~40 per compact = 20
        limit.Should().BeGreaterThan(10, "maximizes coverage within budget");
    }

    [Test]
    [DisplayName("Explore intent biases toward higher limit")]
    public void Given_ExploreIntent_When_Calculate_Then_BiasesHigher()
    {
        // Use 50 results and 600 budget so base limit (15) doesn't hit the ceiling
        var results = Enumerable.Range(0, 50).Select(i => ResultBuilder.Create(60)).ToArray();
        var distribution = DistributionAnalyzer.Analyze(results);

        var exploreLimit = LimitCalculator.Calculate(distribution, Intent.Explore, 600, results.Length);
        var findLimit = LimitCalculator.Calculate(distribution, Intent.Find, 600, results.Length);

        // Base: 600/40 = 15, Explore: 15*1.5 = 22, Find: 15
        exploreLimit.Should().BeGreaterThan(findLimit, "Explore biases toward breadth");
    }

    [Test]
    [DisplayName("Read intent biases toward lower limit")]
    public void Given_ReadIntent_When_Calculate_Then_BiasesLower()
    {
        // Use 50 results and 600 budget so base limit (15) doesn't hit the ceiling
        var results = Enumerable.Range(0, 50).Select(i => ResultBuilder.Create(60)).ToArray();
        var distribution = DistributionAnalyzer.Analyze(results);

        var readLimit = LimitCalculator.Calculate(distribution, Intent.Read, 600, results.Length);
        var findLimit = LimitCalculator.Calculate(distribution, Intent.Find, 600, results.Length);

        // Base: 600/40 = 15, Read: 15*0.5 = 7, Find: 15
        readLimit.Should().BeLessThan(findLimit, "Read biases toward depth (fewer items)");
    }

    [Test]
    [DisplayName("Limit never exceeds total results")]
    public void Given_SmallResultSet_When_Calculate_Then_NeverExceedsTotal()
    {
        var results = new[] { ResultBuilder.Create(80), ResultBuilder.Create(75) };
        var distribution = DistributionAnalyzer.Analyze(results);

        var limit = LimitCalculator.Calculate(distribution, Intent.Explore, 10000, results.Length);

        limit.Should().BeLessThanOrEqualTo(results.Length);
    }

    [Test]
    [DisplayName("Limit is at least 1 for non-empty results")]
    public void Given_SingleResult_When_Calculate_Then_ReturnsAtLeastOne()
    {
        var results = new[] { ResultBuilder.Create(50) };
        var distribution = DistributionAnalyzer.Analyze(results);

        var limit = LimitCalculator.Calculate(distribution, Intent.Read, 100, results.Length);

        limit.Should().BeGreaterThanOrEqualTo(1);
    }
}
