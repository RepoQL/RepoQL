namespace RepoQL.Explore;

/// <summary>
/// Result of analyzing the confidence distribution of search results.
/// </summary>
/// <param name="TopTier">High-confidence results (>= 80% OR >= 75th percentile).</param>
/// <param name="MiddleTier">Medium-confidence results.</param>
/// <param name="BottomTier">Low-confidence results (&lt; 50% AND &lt; 25th percentile).</param>
/// <param name="Shape">Whether the distribution is Lumpy (standouts) or Even (no standouts).</param>
public record DistributionAnalysis(
    IReadOnlyList<ExploreResult> TopTier,
    IReadOnlyList<ExploreResult> MiddleTier,
    IReadOnlyList<ExploreResult> BottomTier,
    DistributionShape Shape
)
{
    /// <summary>
    /// All results in confidence order (top first).
    /// </summary>
    public IEnumerable<ExploreResult> AllResults => TopTier.Concat(MiddleTier).Concat(BottomTier);

    /// <summary>
    /// Total number of results.
    /// </summary>
    public int TotalCount => TopTier.Count + MiddleTier.Count + BottomTier.Count;
}
