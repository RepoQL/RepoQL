namespace RepoQL.Rendering;

/// <summary>
/// Produces rendering decisions for xray results.
/// </summary>
public static class DecisionEngine
{
    private const int WideResultsThreshold = 100;

    /// <summary>
    /// Decide how to render each result.
    /// </summary>
    /// <param name="results">The results to render.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>The rendering decisions for included results.</returns>
    public static DecisionResult Decide(
        IReadOnlyList<XrayResult> results,
        RenderingContext context)
    {
        if (results.Count == 0)
            return new DecisionResult(Array.Empty<RenderingDecision>(), 0, null);

        // Special case: Wide Explore without search criteria → use Minimal (headline only)
        var useMinimalForAll = context.Intent == Intent.Explore
            && !context.HasSearchCriteria
            && results.Count > WideResultsThreshold;

        // Step 1: Analyze distribution
        var distribution = DistributionAnalyzer.Analyze(results);

        // Step 2: Calculate limit (if not provided)
        var limit = context.Limit ?? LimitCalculator.Calculate(
            distribution, context.Intent, context.TokenBudget, results.Count);

        // Step 3: Calculate pressure
        var preferredCost = EstimatePreferredCost(distribution, context.Intent);
        var pressure = (double)preferredCost / context.TokenBudget;

        // Step 3a: Detect limit budget concentration
        // When explicit limit is small and per-item budget is high, reduce pressure to bias toward richer representations
        if (context.Limit.HasValue && context.Limit.Value > 0)
        {
            var perItemBudget = context.TokenBudget / (double)context.Limit.Value;
            const int RichRepresentationThreshold = 200;

            if (perItemBudget >= RichRepresentationThreshold)
            {
                // Reduce pressure to encourage richer representations
                pressure *= 0.5;
            }
        }

        // Step 4: Select strategy
        var strategy = StrategySelector.Select(context.Intent, distribution.Shape, pressure);

        // Step 5: Assign representations to each result based on tier
        var decisions = new List<RenderingDecision>();
        var itemsIncluded = 0;

        foreach (var result in distribution.AllResults)
        {
            if (itemsIncluded >= limit)
                break;

            Representation representation;
            if (useMinimalForAll)
            {
                representation = Representation.Minimal;
            }
            else
            {
                var tier = GetTier(result, distribution);
                var tierRep = GetRepresentationForTier(tier, strategy);

                if (tierRep is null)
                    continue; // Tier is omitted

                representation = tierRep.Value;
            }

            var tokens = TokenEstimator.Estimate(result, representation);
            decisions.Add(new RenderingDecision(result, representation, tokens));
            itemsIncluded++;
        }

        // Step 6: Apply adaptive degradation if over budget
        decisions = ApplyAdaptiveDegradation(decisions, context.TokenBudget);

        // Calculate omitted stats
        var omittedCount = results.Count - decisions.Count;
        Dictionary<string, int>? omittedByType = null;

        if (omittedCount > 0)
        {
            var includedUris = decisions.Select(d => d.Result.Uri).ToHashSet();
            var omittedResults = results.Where(r => !includedUris.Contains(r.Uri)).ToList();

            if (omittedResults.Count > 0)
            {
                // Group by semantic type, using "unknown" as fallback
                omittedByType = omittedResults
                    .GroupBy(r => r.SemanticType ?? "unknown")
                    .OrderByDescending(g => g.Count())
                    .ToDictionary(g => g.Key, g => g.Count());
            }
        }

        return new DecisionResult(decisions, omittedCount, omittedByType);
    }

    private static int EstimatePreferredCost(DistributionAnalysis distribution, Intent intent)
    {
        // Estimate cost at preferred representation levels
        var total = 0;

        foreach (var result in distribution.TopTier)
        {
            var level = intent == Intent.Explore ? Representation.Compact : Representation.Rich;
            total += TokenEstimator.Estimate(result, level);
        }

        foreach (var result in distribution.MiddleTier)
        {
            var level = intent switch
            {
                Intent.Explore => Representation.Compact,
                Intent.Find => Representation.Standard,
                Intent.Read => Representation.Standard,
                _ => Representation.Compact
            };
            total += TokenEstimator.Estimate(result, level);
        }

        foreach (var result in distribution.BottomTier)
        {
            total += TokenEstimator.Estimate(result, Representation.Compact);
        }

        return total;
    }

    private enum Tier { Top, Middle, Bottom }

    private static Tier GetTier(XrayResult result, DistributionAnalysis distribution)
    {
        if (distribution.TopTier.Contains(result))
            return Tier.Top;
        if (distribution.MiddleTier.Contains(result))
            return Tier.Middle;
        return Tier.Bottom;
    }

    private static Representation? GetRepresentationForTier(Tier tier, TierStrategy strategy)
    {
        return tier switch
        {
            Tier.Top => strategy.TopTierLevel,
            Tier.Middle => strategy.MiddleTierLevel,
            Tier.Bottom => strategy.BottomTierLevel,
            _ => null
        };
    }

    private static List<RenderingDecision> ApplyAdaptiveDegradation(
        List<RenderingDecision> decisions,
        int budget)
    {
        var totalTokens = decisions.Sum(d => d.EstimatedTokens);

        if (totalTokens <= budget)
            return decisions;

        // Degrade from the end (lowest confidence) first
        var result = new List<RenderingDecision>(decisions);

        for (var i = result.Count - 1; i >= 0 && totalTokens > budget; i--)
        {
            var current = result[i];
            var degraded = TryDegrade(current);

            if (degraded is not null)
            {
                var saved = current.EstimatedTokens - degraded.EstimatedTokens;
                result[i] = degraded;
                totalTokens -= saved;
            }
        }

        // If still over budget, remove items from the end
        while (result.Count > 1 && totalTokens > budget)
        {
            totalTokens -= result[^1].EstimatedTokens;
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static RenderingDecision? TryDegrade(RenderingDecision decision)
    {
        Representation? lowerLevel = decision.Level switch
        {
            Representation.Rich => Representation.Standard,
            Representation.Standard => Representation.Compact,
            Representation.Compact => Representation.Minimal,
            Representation.Minimal => null, // Can't go lower
            _ => null
        };

        if (lowerLevel is null)
            return null;

        var newTokens = TokenEstimator.Estimate(decision.Result, lowerLevel.Value);
        return new RenderingDecision(decision.Result, lowerLevel.Value, newTokens);
    }
}

/// <summary>
/// Result of the decision process.
/// </summary>
/// <param name="Decisions">The rendering decisions for included items.</param>
/// <param name="OmittedCount">Number of results omitted.</param>
/// <param name="OmittedByType">Omitted items grouped by semantic type (e.g., "markdown.doc" → 25).</param>
public record DecisionResult(
    IReadOnlyList<RenderingDecision> Decisions,
    int OmittedCount,
    IReadOnlyDictionary<string, int>? OmittedByType
);
