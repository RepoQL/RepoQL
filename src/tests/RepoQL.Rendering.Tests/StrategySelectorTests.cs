using AwesomeAssertions;
using RepoQL.Xray;

namespace RepoQL.Rendering.Tests;

public class StrategySelectorTests
{
    // Explore Intent tests

    [Test]
    [DisplayName("Explore + Lumpy + Low: Structure for top, Compact middle, Minimal bottom")]
    public void Given_ExploreLumpyLow_Then_StructureTopCompactMiddleMinimalBottom()
    {
        var strategy = StrategySelector.Select(Intent.Explore, DistributionShape.Lumpy, pressure: 0.5);

        strategy.TopTierLevel.Should().Be(Representation.Standard);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Explore + Lumpy + High: Compact top/middle, Minimal bottom")]
    public void Given_ExploreLumpyHigh_Then_CompactTopMiddleMinimalBottom()
    {
        var strategy = StrategySelector.Select(Intent.Explore, DistributionShape.Lumpy, pressure: 0.8);

        strategy.TopTierLevel.Should().Be(Representation.Compact);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Explore + Even + Low: Compact top/middle, Minimal bottom")]
    public void Given_ExploreEvenLow_Then_CompactTopMiddleMinimalBottom()
    {
        var strategy = StrategySelector.Select(Intent.Explore, DistributionShape.Even, pressure: 0.3);

        strategy.TopTierLevel.Should().Be(Representation.Compact);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Explore + Even + High: Compact top/middle, Minimal bottom")]
    public void Given_ExploreEvenHigh_Then_CompactTopMiddleMinimalBottom()
    {
        var strategy = StrategySelector.Select(Intent.Explore, DistributionShape.Even, pressure: 0.9);

        strategy.TopTierLevel.Should().Be(Representation.Compact);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    // Find Intent tests

    [Test]
    [DisplayName("Find + Lumpy + Low: Rich top, Standard middle, Minimal bottom")]
    public void Given_FindLumpyLow_Then_RichStandardMinimal()
    {
        var strategy = StrategySelector.Select(Intent.Find, DistributionShape.Lumpy, pressure: 0.4);

        strategy.TopTierLevel.Should().Be(Representation.Rich);
        strategy.MiddleTierLevel.Should().Be(Representation.Standard);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Find + Lumpy + High: Rich top, Omit rest")]
    public void Given_FindLumpyHigh_Then_RichTopOmitRest()
    {
        var strategy = StrategySelector.Select(Intent.Find, DistributionShape.Lumpy, pressure: 0.85);

        strategy.TopTierLevel.Should().Be(Representation.Rich);
        strategy.OmitMiddle.Should().BeTrue();
        strategy.OmitBottom.Should().BeTrue();
    }

    [Test]
    [DisplayName("Find + Even + Low: Standard top, Compact middle, Minimal bottom")]
    public void Given_FindEvenLow_Then_StandardCompactMinimal()
    {
        var strategy = StrategySelector.Select(Intent.Find, DistributionShape.Even, pressure: 0.5);

        strategy.TopTierLevel.Should().Be(Representation.Standard);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Find + Even + High: Compact top/middle, Minimal bottom")]
    public void Given_FindEvenHigh_Then_CompactTopMiddleMinimalBottom()
    {
        var strategy = StrategySelector.Select(Intent.Find, DistributionShape.Even, pressure: 0.75);

        strategy.TopTierLevel.Should().Be(Representation.Compact);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    // Read Intent tests

    [Test]
    [DisplayName("Read + Lumpy + Low: Rich top/middle, Minimal bottom")]
    public void Given_ReadLumpyLow_Then_RichRichMinimal()
    {
        var strategy = StrategySelector.Select(Intent.Examine, DistributionShape.Lumpy, pressure: 0.3);

        strategy.TopTierLevel.Should().Be(Representation.Rich);
        strategy.MiddleTierLevel.Should().Be(Representation.Rich);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Read + Lumpy + High: Rich top, Omit rest")]
    public void Given_ReadLumpyHigh_Then_RichTopOmitRest()
    {
        var strategy = StrategySelector.Select(Intent.Examine, DistributionShape.Lumpy, pressure: 0.9);

        strategy.TopTierLevel.Should().Be(Representation.Rich);
        strategy.OmitMiddle.Should().BeTrue();
        strategy.OmitBottom.Should().BeTrue();
    }

    [Test]
    [DisplayName("Read + Even + Low: Rich top, Standard middle, Minimal bottom")]
    public void Given_ReadEvenLow_Then_RichStandardMinimal()
    {
        var strategy = StrategySelector.Select(Intent.Examine, DistributionShape.Even, pressure: 0.4);

        strategy.TopTierLevel.Should().Be(Representation.Rich);
        strategy.MiddleTierLevel.Should().Be(Representation.Standard);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    [Test]
    [DisplayName("Read + Even + High: Rich top, Compact middle, Minimal bottom")]
    public void Given_ReadEvenHigh_Then_RichCompactMinimal()
    {
        var strategy = StrategySelector.Select(Intent.Examine, DistributionShape.Even, pressure: 0.8);

        strategy.TopTierLevel.Should().Be(Representation.Rich);
        strategy.MiddleTierLevel.Should().Be(Representation.Compact);
        strategy.BottomTierLevel.Should().Be(Representation.Minimal);
    }

    // Edge case tests

    [Test]
    [DisplayName("Pressure exactly at threshold (0.7) is High pressure")]
    public void Given_PressureAtThreshold_Then_IsHighPressure()
    {
        var strategy = StrategySelector.Select(Intent.Find, DistributionShape.Lumpy, pressure: 0.7);

        // High pressure for Find+Lumpy omits rest
        strategy.OmitMiddle.Should().BeTrue();
    }

    [Test]
    [DisplayName("Pressure just below threshold is Low pressure")]
    public void Given_PressureBelowThreshold_Then_IsLowPressure()
    {
        var strategy = StrategySelector.Select(Intent.Find, DistributionShape.Lumpy, pressure: 0.69);

        // Low pressure for Find+Lumpy includes all tiers
        strategy.OmitMiddle.Should().BeFalse();
        strategy.OmitBottom.Should().BeFalse();
    }

    // Bottom tier always Minimal test

    [Test]
    [DisplayName("Bottom tier always uses Minimal (headline only)")]
    public void Given_AnyNonOmittingStrategy_Then_BottomTierIsMinimal()
    {
        // Test all non-omitting strategies have Minimal for bottom tier
        var exploreLumpy = StrategySelector.Select(Intent.Explore, DistributionShape.Lumpy, pressure: 0.3);
        var exploreEven = StrategySelector.Select(Intent.Explore, DistributionShape.Even, pressure: 0.3);
        var findEven = StrategySelector.Select(Intent.Find, DistributionShape.Even, pressure: 0.3);
        var readEven = StrategySelector.Select(Intent.Examine, DistributionShape.Even, pressure: 0.3);

        exploreLumpy.BottomTierLevel.Should().Be(Representation.Minimal);
        exploreEven.BottomTierLevel.Should().Be(Representation.Minimal);
        findEven.BottomTierLevel.Should().Be(Representation.Minimal);
        readEven.BottomTierLevel.Should().Be(Representation.Minimal);
    }
}
