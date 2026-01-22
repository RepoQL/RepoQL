namespace RepoQL.Explore;

/// <summary>
/// Calculates optimal limit when not provided.
/// </summary>
public static class LimitCalculator
{
    private const int MiddleTierContextLimit = 5;
    private const int AverageCompactTokenCost = 40;
    private const double ExploreBreadthMultiplier = 1.5;
    private const double ReadDepthMultiplier = 0.5;

    /// <summary>
    /// Calculate optimal limit based on distribution and intent.
    /// </summary>
    /// <param name="distribution">The analyzed distribution of results.</param>
    /// <param name="intent">The agent's intent.</param>
    /// <param name="tokenBudget">Available token budget.</param>
    /// <param name="totalResults">Total number of results available.</param>
    /// <returns>The calculated optimal limit.</returns>
    public static int Calculate(
        DistributionAnalysis distribution,
        Intent intent,
        int tokenBudget,
        int totalResults)
    {
        if (totalResults == 0)
            return 0;

        var baseLimit = distribution.Shape switch
        {
            DistributionShape.Lumpy => CalculateLumpyLimit(distribution),
            DistributionShape.Even => CalculateEvenLimit(tokenBudget, totalResults),
            _ => totalResults
        };

        // Apply intent adjustment
        var adjustedLimit = intent switch
        {
            Intent.Inventory => (int)(baseLimit * ExploreBreadthMultiplier),
            Intent.Inspect => (int)(baseLimit * ReadDepthMultiplier),
            _ => baseLimit
        };

        // Ensure at least 1 result if any exist, and don't exceed total
        return Math.Clamp(adjustedLimit, 1, totalResults);
    }

    private static int CalculateLumpyLimit(DistributionAnalysis distribution)
    {
        // Focus on standouts: top tier + a few middle tier for context
        return distribution.TopTier.Count + Math.Min(distribution.MiddleTier.Count, MiddleTierContextLimit);
    }

    private static int CalculateEvenLimit(int tokenBudget, int totalResults)
    {
        // Maximize coverage: as many as fit in budget at Compact representation
        var budgetLimit = tokenBudget / AverageCompactTokenCost;
        return Math.Min(totalResults, budgetLimit);
    }
}
