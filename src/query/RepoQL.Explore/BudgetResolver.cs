namespace RepoQL.Explore;

/// <summary>
/// Resolution of a budget tier or explicit cap to an effective token budget.
/// </summary>
/// <param name="EffectiveBudget">Tokens the allocator should use.</param>
/// <param name="StatedCap">The agent's original cap (for footer display). Null when tier mode.</param>
public record BudgetResolution(int EffectiveBudget, int? StatedCap);

/// <summary>
/// Resolves explore budget. Explicit int = spend exactly that. Named tier = system
/// picks within a range based on result quality.
/// </summary>
public static class BudgetResolver
{
    private const int ConfidenceThreshold = 35;
    private const int DefaultFloor = 800;
    private const int StructureCost = 150;
    private const int HeadlineOnlyCost = 50;
    private const int MinimalCost = 15;
    private const int ChildCost = 40;
    private const int MaxCountedChildren = 5;

    /// <summary>
    /// Resolve budget from an ExploreQuery. Explicit int passes through unchanged.
    /// Named tier resolves from result quality.
    /// </summary>
    public static BudgetResolution Resolve(
        ExploreQuery query,
        IReadOnlyList<ExploreResult> results,
        bool hasSearchCriteria)
    {
        if (string.IsNullOrWhiteSpace(query.BudgetTier))
        {
            // Explicit budget — spend exactly what was asked
            return new BudgetResolution(query.TokenBudget, null);
        }

        var (min, max) = ParseTierRange(query.BudgetTier);
        return ResolveFromResults(results, min, max, hasSearchCriteria);
    }

    internal static (int Min, int Max) ParseTierRange(string tier)
    {
        return (tier.Trim().ToLowerInvariant()) switch
        {
            "low" => (800, 1500),
            "medium" => (1500, 3500),
            "high" => (3000, 6000),
            _ => (1500, 3500), // default to medium for unrecognized
        };
    }

    private static BudgetResolution ResolveFromResults(
        IReadOnlyList<ExploreResult> results,
        int rangeMin,
        int rangeMax,
        bool hasSearchCriteria)
    {
        if (results.Count == 0)
            return new BudgetResolution(rangeMin, rangeMax);

        if (!hasSearchCriteria)
        {
            var inventoryBudget = Math.Clamp(results.Count * 15, rangeMin, rangeMax);
            return new BudgetResolution(inventoryBudget, rangeMax);
        }

        var qualified = results.Where(r => r.Confidence >= ConfidenceThreshold).ToList();
        if (qualified.Count == 0)
            return new BudgetResolution(rangeMin, rangeMax);

        var avgCostPerResult = qualified.Average(EstimateNaturalDemand);
        var topConfidence = qualified.Max(r => r.Confidence);

        var relativeScores = qualified
            .Select(r => r.Confidence / (double)topConfidence)
            .ToList();

        var sum = relativeScores.Sum();
        var sumOfSquares = relativeScores.Sum(score => score * score);
        var effectiveCount = sumOfSquares <= 0 ? 0 : (sum * sum) / sumOfSquares;
        var concentratedDemand = effectiveCount * avgCostPerResult;
        var qualityMultiplier = 0.8 + (topConfidence / 100.0) * 0.4;
        var rawBudget = (int)(concentratedDemand * qualityMultiplier);

        var effective = Math.Clamp(rawBudget, rangeMin, rangeMax);
        return new BudgetResolution(effective, rangeMax);
    }

    private static double EstimateNaturalDemand(ExploreResult result)
    {
        var baseCost = !string.IsNullOrWhiteSpace(result.Structure)
            ? StructureCost
            : !string.IsNullOrWhiteSpace(result.Headline)
                ? HeadlineOnlyCost
                : MinimalCost;

        var childCount = Math.Min(result.ChildObjects?.Count ?? 0, MaxCountedChildren);
        return baseCost + (childCount * ChildCost);
    }
}
