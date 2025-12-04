namespace RepoQL.Rendering;

/// <summary>
/// The shape of the confidence distribution.
/// </summary>
public enum DistributionShape
{
    /// <summary>
    /// Standouts exist: top tier is small (&lt;20% of results), clear gap to rest.
    /// Strategy: focus on standouts, drop weak matches under pressure.
    /// </summary>
    Lumpy,

    /// <summary>
    /// No standouts: scores clustered within ~20% range.
    /// Strategy: maximize coverage, show headlines for discovery.
    /// </summary>
    Even
}
