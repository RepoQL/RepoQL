using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Rendering.Tests.TestData;

namespace RepoQL.Rendering.Tests;

public class BreadthResolverTests
{
    [Test]
    [DisplayName("Lumpy: one dominant result gets steep sigmoid, all results visible")]
    public void Given_OneDominantResult_When_Resolving_Then_SteepBreadthGenerousLimit()
    {
        var resolution = Resolve([98, 20, 15, 12, 10], effectiveBudget: 3000);

        resolution.EffectiveBreadth.Should().Be(2);
        // Generous limit: all 5 results visible (sigmoid concentrates budget on top)
        resolution.EffectiveLimit.Should().Be(5);
    }

    [Test]
    [DisplayName("Lumpy: clear top cluster gets steep sigmoid, all results visible")]
    public void Given_ClearTopCluster_When_Resolving_Then_SteepBreadthAllVisible()
    {
        var resolution = Resolve([90, 88, 85, 40, 35, 30], effectiveBudget: 3000);

        // Natural group of 3 → breadth 2 (steep sigmoid concentrates on cluster)
        resolution.EffectiveBreadth.Should().Be(2);
        // All 6 results visible — tail degrades to Minimal, not invisible
        resolution.EffectiveLimit.Should().Be(6);
    }

    [Test]
    [DisplayName("Smooth gradient keeps a balanced breadth")]
    public void Given_SmoothGradient_When_Resolving_Then_UsesBalancedBreadth()
    {
        var resolution = Resolve([80, 75, 70, 65, 60, 55, 50, 45, 40], effectiveBudget: 3000);

        resolution.EffectiveBreadth.Should().BeInRange(4, 5);
        resolution.EffectiveLimit.Should().Be(9);
    }

    [Test]
    [DisplayName("Flat moderate distribution resolves to broad coverage")]
    public void Given_FlatDistribution_When_Resolving_Then_BreadthIsHigh()
    {
        var confidences = Enumerable.Repeat(55, 33).ToArray();

        var resolution = Resolve(confidences, effectiveBudget: 8000);

        resolution.EffectiveBreadth.Should().BeInRange(8, 9);
        resolution.EffectiveLimit.Should().Be(33);
    }

    [Test]
    [DisplayName("Tight budget limits how many results can be shown")]
    public void Given_ManyQualifiedResults_When_BudgetIsTight_Then_LimitIsCappedByBudget()
    {
        var resolution = Resolve(Enumerable.Repeat(60, 40).ToArray(), effectiveBudget: 1500);

        // 1500 / 80 (CompactCost) = 18 — generous but bounded
        resolution.EffectiveLimit.Should().BeLessThanOrEqualTo(18);
        // 40 results, no gap → high breadth
        resolution.EffectiveBreadth.Should().Be(9);
    }

    [Test]
    [DisplayName("Inventory scans default to broad coverage")]
    public void Given_NoSearchCriteria_When_Resolving_Then_UsesInventoryMode()
    {
        var resolution = Resolve(Enumerable.Repeat(10, 50).ToArray(), effectiveBudget: 1600, hasSearchCriteria: false);

        resolution.EffectiveBreadth.Should().Be(8);
        resolution.EffectiveLimit.Should().Be(20);
    }

    [Test]
    [DisplayName("No qualified results fall back to balanced breadth")]
    public void Given_NoQualifiedResults_When_Resolving_Then_UsesFallback()
    {
        var resolution = Resolve([34, 30, 25, 20, 15], effectiveBudget: 3000);

        resolution.EffectiveBreadth.Should().Be(5);
        resolution.EffectiveLimit.Should().Be(5);
    }

    [Test]
    [DisplayName("User limit caps resolved auto limit")]
    public void Given_UserLimit_When_Resolving_Then_UserLimitWins()
    {
        var resolution = Resolve(Enumerable.Repeat(60, 40).ToArray(), effectiveBudget: 4000, userLimit: 5);

        resolution.EffectiveLimit.Should().Be(5);
    }

    [Test]
    [DisplayName("Outlier plus noise: steep sigmoid, all results visible")]
    public void Given_OutlierPlusNoise_When_Resolving_Then_SteepBreadthAllVisible()
    {
        var resolution = Resolve([98, 15, 14, 13, 12, 11, 10], effectiveBudget: 3000);

        resolution.EffectiveBreadth.Should().Be(2);
        // All 7 visible — outlier gets depth, noise gets Minimal
        resolution.EffectiveLimit.Should().Be(7);
    }

    [Test]
    [DisplayName("Two clusters: breadth from first gap, all results visible")]
    public void Given_TwoClusters_When_Resolving_Then_SteepBreadthAllVisible()
    {
        var resolution = Resolve([90, 88, 50, 48, 20, 18], effectiveBudget: 3000);

        resolution.EffectiveBreadth.Should().Be(2);
        // All 6 visible — top cluster gets depth, rest degrade gracefully
        resolution.EffectiveLimit.Should().Be(6);
    }

    [Test]
    [DisplayName("Zero results resolve to balanced breadth and zero limit")]
    public void Given_ZeroResults_When_Resolving_Then_ReturnsEmptyResolution()
    {
        var resolution = BreadthResolver.Resolve([], effectiveBudget: 3000, hasSearchCriteria: true, userLimit: null);

        resolution.EffectiveBreadth.Should().Be(5);
        resolution.EffectiveLimit.Should().Be(0);
    }

    [Test]
    [Arguments(new[] { 98, 20, 15, 12, 10 }, 1)]
    [Arguments(new[] { 90, 88, 85, 40, 35 }, 3)]
    [Arguments(new[] { 80, 75, 70, 65, 60 }, 5)]
    [Arguments(new[] { 90 }, 1)]
    [DisplayName("FindNaturalGroupSize detects the first significant gap")]
    public void Given_SortedConfidences_When_FindingNaturalGroupSize_Then_ReturnsExpectedBoundary(int[] confidences, int expected)
    {
        BreadthResolver.FindNaturalGroupSize(confidences).Should().Be(expected);
    }

    [Test]
    [Arguments(1, 2)]
    [Arguments(3, 2)]
    [Arguments(4, 3)]
    [Arguments(5, 3)]
    [Arguments(8, 4)]
    [Arguments(12, 5)]
    [Arguments(18, 6)]
    [Arguments(25, 7)]
    [Arguments(35, 8)]
    [Arguments(36, 9)]
    [DisplayName("MapLimitToBreadth follows the configured buckets")]
    public void Given_Limit_When_MappingBreadth_Then_UsesExpectedBucket(int limit, int expectedBreadth)
    {
        BreadthResolver.MapLimitToBreadth(limit).Should().Be(expectedBreadth);
    }

    private static BreadthResolution Resolve(
        IReadOnlyList<int> confidences,
        int effectiveBudget,
        bool hasSearchCriteria = true,
        int? userLimit = null)
    {
        var results = confidences
            .Select((confidence, index) => ResultBuilder.Document(
                confidence,
                headlineLength: 60,
                structureLength: 140) with
            {
                Uri = $"file:///test/result{index}.cs"
            })
            .ToList();

        return BreadthResolver.Resolve(results, effectiveBudget, hasSearchCriteria, userLimit);
    }
}
