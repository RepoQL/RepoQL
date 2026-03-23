namespace RepoQL.Explore;

public record BreadthResolution(int EffectiveBreadth, int EffectiveLimit);

public static class BreadthResolver
{
    private const int ConfidenceThreshold = 35;
    private const int CompactCost = 80;
    private const int MinGapSize = 10;
    private const double GapMultiplier = 2.0;

    public static BreadthResolution Resolve(
        IReadOnlyList<ExploreResult> results,
        int effectiveBudget,
        bool hasSearchCriteria,
        int? userLimit)
    {
        if (results.Count == 0)
            return new BreadthResolution(5, 0);

        int effectiveLimit;
        int effectiveBreadth;

        if (!hasSearchCriteria)
        {
            effectiveBreadth = 8;
            effectiveLimit = Math.Max(1, effectiveBudget / CompactCost);
        }
        else
        {
            var qualifiedConfidences = results
                .Where(r => r.Confidence >= ConfidenceThreshold)
                .OrderByDescending(r => r.Confidence)
                .Select(r => r.Confidence)
                .ToList();

            if (qualifiedConfidences.Count == 0)
            {
                effectiveBreadth = 5;
                effectiveLimit = Math.Min(10, results.Count);
            }
            else
            {
                var naturalGroupSize = FindNaturalGroupSize(qualifiedConfidences);

                // Breadth from distribution shape: steep sigmoid for lumpy, gentle for smooth
                effectiveBreadth = MapLimitToBreadth(naturalGroupSize);

                // Generous limit: show as many as budget can fund at Compact level.
                // The sigmoid concentrates budget on the top cluster; tail degrades
                // to Minimal (URI-only), preserving awareness without wasting tokens.
                effectiveLimit = Math.Min(
                    Math.Max(1, effectiveBudget / CompactCost),
                    results.Count);
            }
        }

        if (userLimit is > 0)
            effectiveLimit = Math.Min(effectiveLimit, userLimit.Value);

        effectiveLimit = Math.Min(effectiveLimit, results.Count);

        return new BreadthResolution(effectiveBreadth, effectiveLimit);
    }

    internal static int FindNaturalGroupSize(IReadOnlyList<int> sortedConfidences)
    {
        if (sortedConfidences.Count <= 1)
            return sortedConfidences.Count;

        var gaps = new List<int>(sortedConfidences.Count - 1);
        for (var i = 0; i < sortedConfidences.Count - 1; i++)
        {
            gaps.Add(Math.Max(0, sortedConfidences[i] - sortedConfidences[i + 1]));
        }

        if (gaps.Count == 0)
            return sortedConfidences.Count;

        var orderedGaps = gaps.OrderBy(g => g).ToList();
        double medianGap = orderedGaps.Count % 2 == 1
            ? orderedGaps[orderedGaps.Count / 2]
            : (orderedGaps[(orderedGaps.Count / 2) - 1] + orderedGaps[orderedGaps.Count / 2]) / 2.0;

        var significantGapThreshold = Math.Max(MinGapSize, medianGap * GapMultiplier);
        for (var i = 0; i < gaps.Count; i++)
        {
            if (gaps[i] >= significantGapThreshold)
                return i + 1;
        }

        return sortedConfidences.Count;
    }

    internal static int MapLimitToBreadth(int limit)
    {
        if (limit <= 3) return 2;
        if (limit <= 5) return 3;
        if (limit <= 8) return 4;
        if (limit <= 12) return 5;
        if (limit <= 18) return 6;
        if (limit <= 25) return 7;
        if (limit <= 35) return 8;
        return 9;
    }
}
