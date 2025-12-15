namespace RepoQL.Xray;

/// <summary>
/// Representation strategy for each tier.
/// </summary>
/// <param name="TopTierLevel">Representation for high-confidence results.</param>
/// <param name="MiddleTierLevel">Representation for medium-confidence results (null = omit).</param>
/// <param name="BottomTierLevel">Representation for low-confidence results (null = omit).</param>
public record TierStrategy(
    Representation TopTierLevel,
    Representation? MiddleTierLevel,
    Representation? BottomTierLevel
)
{
    /// <summary>
    /// Whether to omit middle tier results.
    /// </summary>
    public bool OmitMiddle => MiddleTierLevel is null;

    /// <summary>
    /// Whether to omit bottom tier results.
    /// </summary>
    public bool OmitBottom => BottomTierLevel is null;
}
