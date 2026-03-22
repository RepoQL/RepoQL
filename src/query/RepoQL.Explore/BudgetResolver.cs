namespace RepoQL.Explore;

public record BudgetResolution(int EffectiveBudget, int StatedCap);

/// <summary>
/// Resolves an effective explore budget beneath the stated cap when the result set
/// does not warrant spending the full amount.
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

    public static BudgetResolution Resolve(
        IReadOnlyList<ExploreResult> results,
        int statedCap,
        bool hasSearchCriteria)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(statedCap);

        if (results.Count == 0)
            return new BudgetResolution(statedCap, statedCap);

        if (!hasSearchCriteria)
            return new BudgetResolution(ClampBudget(results.Count * 15, statedCap), statedCap);

        var qualified = results.Where(r => r.Confidence >= ConfidenceThreshold).ToList();
        if (qualified.Count == 0)
            return new BudgetResolution(ClampBudget(DefaultFloor, statedCap), statedCap);

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

        return new BudgetResolution(ClampBudget(rawBudget, statedCap), statedCap);
    }

    private static int EstimateNaturalDemand(ExploreResult result)
    {
        var baseCost = !string.IsNullOrWhiteSpace(result.Structure)
            ? StructureCost
            : !string.IsNullOrWhiteSpace(result.Headline)
                ? HeadlineOnlyCost
                : MinimalCost;

        var childCount = Math.Min(result.ChildObjects?.Count ?? 0, MaxCountedChildren);
        return baseCost + (childCount * ChildCost);
    }

    private static int ClampBudget(int value, int statedCap)
    {
        var floor = Math.Min(DefaultFloor, statedCap);
        return Math.Clamp(value, floor, statedCap);
    }
}
