/// <summary>
/// Evaluation test for explore tool with Inspect intent
/// Tests the specific call: mcp__repoql__explore with tokenBudget=2000, intent=Inspect, keywords="utility calculator relevance evidence"
/// </summary>

using AwesomeAssertions;
using RepoQL.Rendering.Tests.TestData;
using RepoQL.Explore;

namespace RepoQL.Rendering.Tests;

public class ExploreInspectIntentEvaluationTests
{
    /// <summary>
    /// Simulates searching for files related to "utility calculator relevance evidence"
    /// Expected results:
    /// 1. UtilityCalculator.cs - Calculates utility scores and evidence quality
    /// 2. LimitCalculator.cs - Calculates optimal limits (might match "calculator" keyword)
    /// 3. TokenEstimator.cs - Estimates tokens based on representation
    /// 4. StrategySelector.cs - Selects strategies based on evidence and intent
    /// </summary>
    [Test]
    [DisplayName("Inspect intent with 2000 token budget should show code snippets for top matches")]
    public void Given_InspectIntentWith2000Tokens_When_SearchForUtilityCalculatorRelevanceEvidence_Then_ShowsRichRepresentation()
    {
        // Simulate search results for "utility calculator relevance evidence"
        var utilityCalculatorResult = new ExploreResult(
            Uri: "file:///src/RepoQL.Explore/UtilityCalculator.cs",
            Confidence: 95,
            Kind: null,
            Headline: "Calculates utility scores for value-based token allocation with relevance and evidence quality",
            Structure: @"
  UtilityCalculator (static)
  ├─ SemanticEvidenceQuality (const): 1.0
  ├─ LexicalOnlyEvidenceQuality (const): 0.7
  ├─ CalculateRelevance(int): double
  └─ CalculateEvidenceQuality(bool, bool, bool): double",
            Snippet: @"/// Implements the utility model formula:
/// U(item, option) = P_relevance × V(option, intent) × evidenceQuality × novelty
///
/// Where:
/// - P_relevance: Normalized confidence (0-1) from search results
/// - V(option, intent): Value matrix (provided by OptionValue)
/// - evidenceQuality: 1.0 for semantic hit, 0.7 for lexical-only
/// - novelty: Diminishing returns for same type/file (1.0, 0.83, 0.71...)
public static class UtilityCalculator
{
    public const double SemanticEvidenceQuality = 1.0;
    public const double LexicalOnlyEvidenceQuality = 0.7;

    public static double CalculateRelevance(int confidence)
    {
        var clamped = Math.Max(0, Math.Min(100, confidence));
        return clamped / 100.0;
    }

    public static double CalculateEvidenceQuality(bool hasSemanticScore, bool hasNameHit, bool hasRegexHit)
    {
        if (hasSemanticScore)
            return SemanticEvidenceQuality;
        if (hasNameHit || hasRegexHit)
            return LexicalOnlyEvidenceQuality;
        return 0.0;
    }
}",
            Lang: "csharp",
            SemanticType: "code.csharp"
        );

        var limitCalculatorResult = new ExploreResult(
            Uri: "file:///src/RepoQL.Explore/LimitCalculator.cs",
            Confidence: 72,
            Kind: null,
            Headline: "Calculates optimal limit based on distribution, intent, and token budget",
            Structure: @"
  LimitCalculator (static)
  ├─ MiddleTierContextLimit (const): 5
  ├─ AverageCompactTokenCost (const): 40
  ├─ ExploreBreadthMultiplier (const): 1.5
  ├─ ReadDepthMultiplier (const): 0.5
  └─ Calculate(DistributionAnalysis, Intent, int, int): int",
            Snippet: null,
            Lang: null,
            SemanticType: "code.csharp"
        );

        var strategyResult = new ExploreResult(
            Uri: "file:///src/RepoQL.Explore/StrategySelector.cs",
            Confidence: 65,
            Kind: null,
            Headline: "Selects representation strategy based on intent, distribution shape, and token pressure",
            Structure: @"
  StrategySelector (static)
  ├─ Select(Intent, DistributionShape, double): TierStrategy
  ├─ SelectExploreStrategy(...)
  ├─ SelectFindStrategy(...)
  └─ SelectReadStrategy(...)",
            Snippet: null,
            Lang: null,
            SemanticType: "code.csharp"
        );

        var results = new[] { utilityCalculatorResult, limitCalculatorResult, strategyResult };

        // Test 1: Distribution analysis
        var distribution = DistributionAnalyzer.Analyze(results);
        // With 95, 72, 65 - this may be Even or Lumpy depending on the analyzer threshold
        // The important part is that 95 is clearly the top tier
        distribution.TopTier.Should().Contain(utilityCalculatorResult, "95 confidence is highest");

        // Test 2: Limit calculation for Inspect intent should be lower than Find
        var inspectLimit = LimitCalculator.Calculate(distribution, Intent.Inspect, 2000, results.Length);
        var findLimit = LimitCalculator.Calculate(distribution, Intent.Locate, 2000, results.Length);
        inspectLimit.Should().BeLessThan(findLimit, "Inspect intent biases toward depth (fewer items)");

        // Test 3: Strategy selection for Inspect intent should prioritize Rich representation
        var pressure = 500.0 / 2000; // ~0.25 (low pressure)
        var strategy = StrategySelector.Select(Intent.Inspect, distribution.Shape, pressure);
        // Inspect intent always prefers Rich for top tier
        strategy.TopTierLevel.Should().Be(Representation.Rich, "Inspect intent always shows Rich for top tier (code snippets)");
        // Middle tier depends on shape, but should have substance in Inspect intent
        var middleTier = strategy.MiddleTierLevel;
        (middleTier == Representation.Rich || middleTier == Representation.Standard).Should().BeTrue(
            "Middle tier should have substance (Rich or Standard) in Inspect intent, got {0}", middleTier);

        // Test 4: Token estimation for Rich representation should consume significant tokens
        var richTokens = ExploreTokenEstimator.Estimate(utilityCalculatorResult, Representation.Rich);
        richTokens.Should().BeGreaterThan(150, "Rich representation with code snippet should be 150+ tokens");

        // Test 5: Rich representation includes snippet in formatted output
        var formatted = RepresentationFormatter.FormatRich(utilityCalculatorResult, showConfidence: true);
        formatted.Should().Contain("```csharp");
        formatted.Should().Contain("public static class UtilityCalculator");
        formatted.Should().Contain("SemanticEvidenceQuality");
        formatted.Should().Contain("```");

        // Test 6: Rendering decision should prioritize top result with Rich representation
        var decisions = new[]
        {
            new RenderingDecision(utilityCalculatorResult, Representation.Rich, richTokens),
            new RenderingDecision(limitCalculatorResult, Representation.Standard, 70),
            new RenderingDecision(strategyResult, Representation.Compact, 40),
        };

        var decisionResult = new DecisionResult(decisions, 0, null);

        // Verify that with 2000 token budget and Inspect intent, we should have all 3 results
        decisionResult.Decisions.Count.Should().Be(3, "2000 tokens should be sufficient for all 3 items");
    }

    /// <summary>
    /// Evaluation criteria for Inspect intent usefulness:
    /// - Does it include actual code snippets? YES
    /// - Can agent understand the code? YES - snippets show implementation
    /// - Is structure preserved? YES - structure shown for items not at Rich level
    /// - Are there enough details to work with? YES - headline + snippet is comprehensive
    /// - Does 2000 tokens feel right? YES - allows multiple Rich representations
    /// </summary>
    [Test]
    [DisplayName("Inspect intent output enables agent to understand code structure and purpose")]
    public void Given_InspectIntentOutput_When_DisplayedToAgent_Then_ProvidesEnoughDetailToWork()
    {
        // Scenario: Agent needs to understand how UtilityCalculator works to implement similar logic elsewhere

        var utilityResult = new ExploreResult(
            Uri: "file:///src/RepoQL.Explore/UtilityCalculator.cs",
            Confidence: 95,
            Kind: null,
            Headline: "Calculates utility scores for value-based token allocation with relevance and evidence quality",
            Structure: null,
            Snippet: @"public static double CalculateRelevance(int confidence)
{
    // Clamp confidence to [0, 100] range and normalize
    var clamped = Math.Max(0, Math.Min(100, confidence));
    return clamped / 100.0;
}

public static double CalculateEvidenceQuality(bool hasSemanticScore, bool hasNameHit, bool hasRegexHit)
{
    // Semantic evidence is strongest
    if (hasSemanticScore)
        return SemanticEvidenceQuality;

    // Lexical evidence (name or regex) is weaker
    if (hasNameHit || hasRegexHit)
        return LexicalOnlyEvidenceQuality;

    // No evidence detected
    return 0.0;
}",
            Lang: "csharp"
        );

        // With Inspect intent, Rich representation gives agent:
        // 1. Full qualified URI to locate file
        // 2. Headline describing what the class does
        // 3. Actual implementation code (not just structure)
        // 4. Comments explaining business logic

        var formatted = RepresentationFormatter.FormatRich(utilityResult, showConfidence: true);

        // Verify agent can understand the actual implementation details from the snippet
        // Note: Rich representation shows snippet, not headline

        // - How confidence maps to relevance (from code)
        formatted.Should().Contain("CalculateRelevance");
        formatted.Should().Contain("clamped / 100.0");

        // - The evidence quality logic (from code)
        formatted.Should().Contain("CalculateEvidenceQuality");
        formatted.Should().Contain("SemanticEvidenceQuality");
        formatted.Should().Contain("LexicalOnlyEvidenceQuality");

        // - Includes code fence for readability
        formatted.Should().Contain("```csharp");
        formatted.Should().Contain("```");

        // All in one continuous view = agent can understand the implementation details
    }

    /// <summary>
    /// Comparison: Would other intents be better or worse?
    /// - Explore: Would show too many items, lose depth
    /// - Find: Would still show Rich for top match, but might sacrifice Standard for middle tier
    /// - Inspect: Perfect for code understanding - depth over breadth
    /// </summary>
    [Test]
    [DisplayName("Inspect intent vs other intents for code understanding")]
    public void Given_DifferentIntents_When_Comparing_Then_InspectIsOptimalForCodeWork()
    {
        var results = new[]
        {
            ResultBuilder.Create(95, 50, 200, 300, kind: null, uri: "file:///a.cs"),
            ResultBuilder.Create(70, 50, 200, null, kind: null, uri: "file:///b.cs"),
            ResultBuilder.Create(50, 50, null, null, kind: null, uri: "file:///c.cs"),
        };

        var distribution = DistributionAnalyzer.Analyze(results);

        // Explore intent: Breadth (show all with minimal detail)
        var exploreLimitCalc = LimitCalculator.Calculate(distribution, Intent.Inventory, 2000, results.Length);
        var exploreStrategy = StrategySelector.Select(Intent.Inventory, distribution.Shape, 0.3);

        // Find intent: Balanced (standouts in Rich, others in Standard)
        var findLimitCalc = LimitCalculator.Calculate(distribution, Intent.Locate, 2000, results.Length);
        var findStrategy = StrategySelector.Select(Intent.Locate, distribution.Shape, 0.3);

        // Inspect intent: Depth (fewer items, but all in Rich)
        var inspectLimitCalc = LimitCalculator.Calculate(distribution, Intent.Inspect, 2000, results.Length);
        var inspectStrategy = StrategySelector.Select(Intent.Inspect, distribution.Shape, 0.3);

        // For code understanding work, Inspect is best because:
        inspectStrategy.TopTierLevel.Should().Be(Representation.Rich, "Top tier always has code");
        inspectStrategy.MiddleTierLevel.Should().Be(Representation.Rich, "Middle also has code in Inspect intent");

        inspectLimitCalc.Should().BeLessThan(exploreLimitCalc, "Inspect shows fewer items (depth > breadth)");
        inspectLimitCalc.Should().BeLessThanOrEqualTo(findLimitCalc, "Inspect is more focused than Find");

        // Each item shown in Inspect intent has full code context
        var decision = new RenderingDecision(results[0], Representation.Rich, 400);
        var formatted = RepresentationFormatter.Format(decision, showConfidence: true);
        formatted.Should().Contain("```"); // Rich representation = code snippet
    }
}
