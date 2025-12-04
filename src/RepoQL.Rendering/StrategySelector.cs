namespace RepoQL.Rendering;

/// <summary>
/// Selects representation strategy based on Intent × Distribution × Pressure.
/// </summary>
public static class StrategySelector
{
    private const double HighPressureThreshold = 0.7;

    /// <summary>
    /// Select the tier strategy based on context.
    /// </summary>
    /// <param name="intent">The agent's intent.</param>
    /// <param name="shape">The distribution shape.</param>
    /// <param name="pressure">Token pressure ratio (estimated/budget).</param>
    /// <returns>The strategy for each tier.</returns>
    public static TierStrategy Select(Intent intent, DistributionShape shape, double pressure)
    {
        var highPressure = pressure >= HighPressureThreshold;

        return intent switch
        {
            Intent.Explore => SelectExploreStrategy(shape, highPressure),
            Intent.Find => SelectFindStrategy(shape, highPressure),
            Intent.Read => SelectReadStrategy(shape, highPressure),
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };
    }

    private static TierStrategy SelectExploreStrategy(DistributionShape shape, bool highPressure)
    {
        // Explore: Always breadth. Even with standouts, headlines map the territory.
        // Bottom tier always Minimal (headline only) - low confidence doesn't need URI.
        return (shape, highPressure) switch
        {
            (DistributionShape.Lumpy, false) => new TierStrategy(
                TopTierLevel: Representation.Standard,  // Structure for standouts
                MiddleTierLevel: Representation.Compact,
                BottomTierLevel: Representation.Minimal),

            // Lumpy high, or Even (any pressure): Compact for visible, Minimal for bottom
            _ => new TierStrategy(
                TopTierLevel: Representation.Compact,
                MiddleTierLevel: Representation.Compact,
                BottomTierLevel: Representation.Minimal)
        };
    }

    private static TierStrategy SelectFindStrategy(DistributionShape shape, bool highPressure)
    {
        // Find: Adapts to shape. Standouts → depth. No standouts → breadth for discovery.
        // Bottom tier always Minimal (headline only) - low confidence doesn't need URI.
        return (shape, highPressure) switch
        {
            (DistributionShape.Lumpy, false) => new TierStrategy(
                TopTierLevel: Representation.Rich,
                MiddleTierLevel: Representation.Standard,
                BottomTierLevel: Representation.Minimal),

            (DistributionShape.Lumpy, true) => new TierStrategy(
                TopTierLevel: Representation.Rich,
                MiddleTierLevel: null,  // Omit
                BottomTierLevel: null), // Omit

            (DistributionShape.Even, false) => new TierStrategy(
                TopTierLevel: Representation.Standard,
                MiddleTierLevel: Representation.Compact,
                BottomTierLevel: Representation.Minimal),

            (DistributionShape.Even, true) => new TierStrategy(
                TopTierLevel: Representation.Compact,
                MiddleTierLevel: Representation.Compact,
                BottomTierLevel: Representation.Minimal),

            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }

    private static TierStrategy SelectReadStrategy(DistributionShape shape, bool highPressure)
    {
        // Read: Always depth. Fewer items but always with code.
        // Bottom tier always Minimal (headline only) - low confidence doesn't need URI.
        return (shape, highPressure) switch
        {
            (DistributionShape.Lumpy, false) => new TierStrategy(
                TopTierLevel: Representation.Rich,
                MiddleTierLevel: Representation.Rich,
                BottomTierLevel: Representation.Minimal),

            (DistributionShape.Lumpy, true) => new TierStrategy(
                TopTierLevel: Representation.Rich,
                MiddleTierLevel: null,  // Omit
                BottomTierLevel: null), // Omit

            (DistributionShape.Even, false) => new TierStrategy(
                TopTierLevel: Representation.Rich,
                MiddleTierLevel: Representation.Standard,
                BottomTierLevel: Representation.Minimal),

            (DistributionShape.Even, true) => new TierStrategy(
                TopTierLevel: Representation.Rich,
                MiddleTierLevel: Representation.Compact,
                BottomTierLevel: Representation.Minimal),

            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }
}
