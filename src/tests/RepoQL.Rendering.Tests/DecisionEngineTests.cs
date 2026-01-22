using AwesomeAssertions;
using RepoQL.Rendering.Tests.TestData;
using RepoQL.Explore;

namespace RepoQL.Rendering.Tests;

public class DecisionEngineTests
{
    [Test]
    [DisplayName("Empty results returns empty decisions")]
    public void Given_EmptyResults_When_Decide_Then_ReturnsEmpty()
    {
        var context = new RenderingContext(Intent.Locate, TokenBudget: 1000, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(Array.Empty<ExploreResult>(), context);

        result.Decisions.Should().BeEmpty();
        result.OmittedCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Lumpy distribution with high pressure omits bottom tier")]
    public void Given_LumpyHighPressure_When_Decide_Then_OmitsBottomTier()
    {
        var results = new[]
        {
            ResultBuilder.Create(95, snippetLength: 200),
            ResultBuilder.Create(94, snippetLength: 200),
            ResultBuilder.Create(40),
            ResultBuilder.Create(35),
            ResultBuilder.Create(30),
        };
        // Small budget creates high pressure
        var context = new RenderingContext(Intent.Locate, TokenBudget: 200, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        // High pressure Find+Lumpy omits middle and bottom
        result.Decisions.Count.Should().BeLessThan(results.Length);
        result.OmittedCount.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("Even distribution with Find intent shows all tiers")]
    public void Given_EvenDistribution_When_Find_Then_ShowsAllTiers()
    {
        var results = Enumerable.Range(0, 6)
            .Select(i => ResultBuilder.Create(60 + i * 2))
            .ToArray();
        var context = new RenderingContext(Intent.Locate, TokenBudget: 2000, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        // Should include all results
        result.Decisions.Should().HaveCount(results.Length);
        result.OmittedCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Explicit limit caps number of decisions")]
    public void Given_ExplicitLimit_When_Decide_Then_RespectsLimit()
    {
        var results = Enumerable.Range(0, 20)
            .Select(i => ResultBuilder.Create(50 + i))
            .ToArray();
        var context = new RenderingContext(Intent.Inventory, TokenBudget: 5000, Limit: 5, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        result.Decisions.Should().HaveCount(5);
        result.OmittedCount.Should().Be(15);
    }

    [Test]
    [DisplayName("Top tier results come first in decisions")]
    public void Given_MixedResults_When_Decide_Then_TopTierFirst()
    {
        var results = new[]
        {
            ResultBuilder.Create(95),
            ResultBuilder.Create(60),
            ResultBuilder.Create(30),
        };
        var context = new RenderingContext(Intent.Locate, TokenBudget: 2000, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        result.Decisions[0].Result.Confidence.Should().Be(95, "top tier first");
    }

    [Test]
    [DisplayName("Read intent uses Rich representation for top tier")]
    public void Given_ReadIntent_When_Decide_Then_TopTierIsRich()
    {
        var results = new[]
        {
            ResultBuilder.Create(95, snippetLength: 100),
            ResultBuilder.Create(60),
        };
        var context = new RenderingContext(Intent.Inspect, TokenBudget: 2000, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        result.Decisions[0].Level.Should().Be(Representation.Rich);
    }

    [Test]
    [DisplayName("Explore intent uses Compact representation")]
    public void Given_ExploreIntent_When_Decide_Then_UsesCompact()
    {
        var results = new[]
        {
            ResultBuilder.Create(70),
            ResultBuilder.Create(65),
        };
        var context = new RenderingContext(Intent.Inventory, TokenBudget: 2000, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        result.Decisions.Should().AllSatisfy(d =>
            d.Level.Should().Be(Representation.Compact));
    }

    [Test]
    [DisplayName("Adaptive degradation reduces representation when over budget")]
    public void Given_OverBudget_When_Decide_Then_DegradeRepresentation()
    {
        var results = new[]
        {
            ResultBuilder.Create(95, snippetLength: 500), // ~125 tokens
            ResultBuilder.Create(90, snippetLength: 500),
        };
        // Budget too small for two Rich items
        var context = new RenderingContext(Intent.Inspect, TokenBudget: 150, Limit: 2, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        // At least one item should be degraded from Rich
        var totalTokens = result.Decisions.Sum(d => d.EstimatedTokens);
        totalTokens.Should().BeLessThanOrEqualTo(150, "should respect budget");
    }

    [Test]
    [DisplayName("Omitted results track by semantic type")]
    public void Given_OmittedResults_When_Decide_Then_TracksSemanticTypes()
    {
        var results = new[]
        {
            ResultBuilder.Create(95, semanticType: "code.csharp"),
            ResultBuilder.Create(90, semanticType: "code.csharp"),
            ResultBuilder.Create(40, semanticType: "code.csharp"),
            ResultBuilder.Create(30, semanticType: "markdown.doc"),
            ResultBuilder.Create(20, semanticType: "markdown.doc"),
        };
        var context = new RenderingContext(Intent.Inventory, TokenBudget: 2000, Limit: 2, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        result.OmittedCount.Should().Be(3);
        result.OmittedByType.Should().NotBeNull();
        result.OmittedByType!["markdown.doc"].Should().Be(2);
        result.OmittedByType["code.csharp"].Should().Be(1);
    }

    [Test]
    [DisplayName("Total estimated tokens never exceeds budget")]
    public void Given_AnyInput_When_Decide_Then_RespectsBudget()
    {
        var results = Enumerable.Range(0, 50)
            .Select(i => ResultBuilder.Create(20 + i, snippetLength: 100))
            .ToArray();
        var context = new RenderingContext(Intent.Locate, TokenBudget: 500, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        var totalTokens = result.Decisions.Sum(d => d.EstimatedTokens);
        totalTokens.Should().BeLessThanOrEqualTo(500);
    }

    [Test]
    [DisplayName("At least one result when budget is very small")]
    public void Given_TinyBudget_When_Decide_Then_AtLeastOneResult()
    {
        var results = new[] { ResultBuilder.Create(80) };
        var context = new RenderingContext(Intent.Locate, TokenBudget: 10, Limit: null, HasSearchCriteria: true);

        var result = DecisionEngine.Decide(results, context);

        result.Decisions.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Test]
    [DisplayName("Wide Explore without search criteria uses Minimal")]
    public void Given_WideExploreNoSearch_When_Decide_Then_UsesMinimal()
    {
        // Create >100 results to trigger the wide results rule
        var results = Enumerable.Range(0, 150)
            .Select(i => ResultBuilder.Create(50 + i % 30))
            .ToArray();
        var context = new RenderingContext(
            Intent: Intent.Inventory,
            TokenBudget: 5000,
            Limit: null,
            HasSearchCriteria: false);  // No search criteria

        var result = DecisionEngine.Decide(results, context);

        // All items should be Minimal (headline only, no URI)
        result.Decisions.Should().AllSatisfy(d =>
            d.Level.Should().Be(Representation.Minimal));
    }

    [Test]
    [DisplayName("Explore with search criteria uses Compact even with >100 results")]
    public void Given_WideExploreWithSearch_When_Decide_Then_UsesCompact()
    {
        var results = Enumerable.Range(0, 150)
            .Select(i => ResultBuilder.Create(50 + i % 30))
            .ToArray();
        var context = new RenderingContext(
            Intent: Intent.Inventory,
            TokenBudget: 5000,
            Limit: null,
            HasSearchCriteria: true);  // Has search criteria

        var result = DecisionEngine.Decide(results, context);

        // Should use Compact, not Minimal
        result.Decisions.Should().AllSatisfy(d =>
            d.Level.Should().Be(Representation.Compact));
    }

    [Test]
    [DisplayName("Explore without search and <=100 results uses Compact")]
    public void Given_SmallExploreNoSearch_When_Decide_Then_UsesCompact()
    {
        var results = Enumerable.Range(0, 50)
            .Select(i => ResultBuilder.Create(60))
            .ToArray();
        var context = new RenderingContext(
            Intent: Intent.Inventory,
            TokenBudget: 5000,
            Limit: null,
            HasSearchCriteria: false);  // No search criteria, but <=100 results

        var result = DecisionEngine.Decide(results, context);

        // Should use Compact, not Minimal (under threshold)
        result.Decisions.Should().AllSatisfy(d =>
            d.Level.Should().Be(Representation.Compact));
    }
}
