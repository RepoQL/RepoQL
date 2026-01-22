namespace RepoQL.Explore;

/// <summary>
/// Analyzes the confidence distribution of search results.
/// Classifies results into tiers and detects distribution shape.
/// </summary>
public static class DistributionAnalyzer
{
    private const int AbsoluteTopThreshold = 80;
    private const int AbsoluteBottomThreshold = 50;
    private const double TopPercentile = 0.75;
    private const double BottomPercentile = 0.25;
    private const double LumpyTopTierMaxRatio = 0.20;
    private const int EvenDistributionMaxSpread = 20;

    /// <summary>
    /// Analyze the distribution of results.
    /// </summary>
    public static DistributionAnalysis Analyze(IReadOnlyList<ExploreResult> results)
    {
        if (results.Count == 0)
        {
            return new DistributionAnalysis(
                Array.Empty<ExploreResult>(),
                Array.Empty<ExploreResult>(),
                Array.Empty<ExploreResult>(),
                DistributionShape.Even
            );
        }

        // Sort by confidence descending
        var sorted = results.OrderByDescending(r => r.Confidence).ToList();

        // Calculate percentile thresholds
        var p75 = GetPercentileValue(sorted, TopPercentile);
        var p25 = GetPercentileValue(sorted, BottomPercentile);

        // Classify into tiers
        var topTier = new List<ExploreResult>();
        var middleTier = new List<ExploreResult>();
        var bottomTier = new List<ExploreResult>();

        foreach (var result in sorted)
        {
            var tier = ClassifyTier(result.Confidence, p75, p25);
            switch (tier)
            {
                case Tier.Top:
                    topTier.Add(result);
                    break;
                case Tier.Bottom:
                    bottomTier.Add(result);
                    break;
                default:
                    middleTier.Add(result);
                    break;
            }
        }

        // Detect distribution shape
        var shape = DetectShape(sorted, topTier.Count);

        return new DistributionAnalysis(topTier, middleTier, bottomTier, shape);
    }

    private enum Tier { Top, Middle, Bottom }

    private static Tier ClassifyTier(int confidence, int p75, int p25)
    {
        // Top: >= 80% OR >= 75th percentile
        if (confidence >= AbsoluteTopThreshold || confidence >= p75)
            return Tier.Top;

        // Bottom: < 50% AND < 25th percentile
        if (confidence < AbsoluteBottomThreshold && confidence < p25)
            return Tier.Bottom;

        return Tier.Middle;
    }

    private static DistributionShape DetectShape(List<ExploreResult> sorted, int topTierCount)
    {
        if (sorted.Count <= 1)
            return DistributionShape.Even;

        // Check if scores are clustered within ~20% range (Even)
        var maxConfidence = sorted[0].Confidence;
        var minConfidence = sorted[^1].Confidence;
        var spread = maxConfidence - minConfidence;

        if (spread <= EvenDistributionMaxSpread)
            return DistributionShape.Even;

        // Check if top tier is small (< 20% of results) = Lumpy
        var topTierRatio = (double)topTierCount / sorted.Count;
        if (topTierRatio < LumpyTopTierMaxRatio)
            return DistributionShape.Lumpy;

        // Default to Lumpy if there's significant spread but top tier isn't tiny
        // This handles cases where there are clear standouts even if >20%
        return spread > 30 ? DistributionShape.Lumpy : DistributionShape.Even;
    }

    private static int GetPercentileValue(List<ExploreResult> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;

        // sorted is descending, so we need to invert the percentile
        var index = (int)((1 - percentile) * (sorted.Count - 1));
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)].Confidence;
    }
}
