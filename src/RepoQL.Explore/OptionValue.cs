namespace RepoQL.Explore;

/// <summary>
/// Representation level for result output.
/// Each level includes progressively more information for decision making.
/// </summary>
public enum RepresentationLevel
{
    /// <summary>
    /// Just the URI and kind. Minimal context. ~5-10 tokens.
    /// </summary>
    Minimal,

    /// <summary>
    /// URI + headline (single line). ~20-40 tokens.
    /// </summary>
    Compact,

    /// <summary>
    /// URI + headline + structure. ~70-200 tokens.
    /// </summary>
    Standard,

    /// <summary>
    /// URI + headline + structure + snippet/body. ~150-500 tokens.
    /// </summary>
    Rich
}

/// <summary>
/// Value matrix mapping intents to representation options.
/// Determines how valuable different representation levels are for different intents.
/// </summary>
/// <remarks>
/// Value matrix:
///
/// Intent/Option    | Minimal | Compact | Standard | Rich
/// -----------------+---------+---------+----------+------
/// Explore          |  0.8    |  0.4    |   0.2    | 0.1
/// Find             |  0.6    |  0.7    |   0.5    | 0.3
/// Read             |  0.4    |  0.6    |   0.8    | 0.7
///
/// The value represents the marginal value of adding that representation level
/// to the result set given the agent's intent. Higher values indicate more valuable representations.
/// </remarks>
public static class OptionValue
{
    /// <summary>
    /// Gets the value (0-1) of a representation level for a given intent.
    /// Higher values indicate more valuable representations for that intent.
    /// </summary>
    /// <param name="intent">The agent's intent for the explore operation.</param>
    /// <param name="level">The representation level being evaluated.</param>
    /// <returns>A value between 0 and 1 indicating the marginal value.</returns>
    public static double GetValue(Intent intent, RepresentationLevel level)
    {
        return (intent, level) switch
        {
            // Explore: breadth over depth. Minimal is most valuable for showing inventory.
            (Intent.Inventory, RepresentationLevel.Minimal) => 0.8,
            (Intent.Inventory, RepresentationLevel.Compact) => 0.4,
            (Intent.Inventory, RepresentationLevel.Standard) => 0.2,
            (Intent.Inventory, RepresentationLevel.Rich) => 0.1,

            // Find: adapts to distribution. Compact and Standard are most valuable for locating things.
            (Intent.Locate, RepresentationLevel.Minimal) => 0.6,
            (Intent.Locate, RepresentationLevel.Compact) => 0.7,
            (Intent.Locate, RepresentationLevel.Standard) => 0.5,
            (Intent.Locate, RepresentationLevel.Rich) => 0.3,

            // Read: depth over breadth. Standard and Rich are most valuable for code viewing.
            (Intent.Inspect, RepresentationLevel.Minimal) => 0.4,
            (Intent.Inspect, RepresentationLevel.Compact) => 0.6,
            (Intent.Inspect, RepresentationLevel.Standard) => 0.8,
            (Intent.Inspect, RepresentationLevel.Rich) => 0.7,

            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown intent")
        };
    }

    /// <summary>
    /// Gets the estimated token cost difference for upgrading from one representation level to another.
    /// </summary>
    /// <param name="from">The source representation level.</param>
    /// <param name="to">The target representation level.</param>
    /// <returns>The estimated number of additional tokens required for the upgrade.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the upgrade is not a valid progression or if levels are equal.</exception>
    public static int GetUpgradeCost(RepresentationLevel from, RepresentationLevel to)
    {
        if (from == to)
            throw new ArgumentException("Cannot calculate upgrade cost for the same representation level", nameof(to));

        // Valid upgrade progressions (from -> to)
        return (from, to) switch
        {
            // Minimal -> Compact: add URI + headline formatting
            (RepresentationLevel.Minimal, RepresentationLevel.Compact) => 30,

            // Minimal -> Standard: add URI + headline + structure
            (RepresentationLevel.Minimal, RepresentationLevel.Standard) => 110,

            // Minimal -> Rich: add URI + headline + structure + snippet
            (RepresentationLevel.Minimal, RepresentationLevel.Rich) => 260,

            // Compact -> Standard: add structure
            (RepresentationLevel.Compact, RepresentationLevel.Standard) => 80,

            // Compact -> Rich: add structure + snippet
            (RepresentationLevel.Compact, RepresentationLevel.Rich) => 230,

            // Standard -> Rich: add snippet (with some structure reduction)
            (RepresentationLevel.Standard, RepresentationLevel.Rich) => 150,

            // Downgrade attempts (invalid)
            _ => throw new ArgumentException(
                $"Invalid representation upgrade: {from} -> {to}. Can only upgrade to higher detail levels.",
                nameof(to))
        };
    }

    /// <summary>
    /// Determines if an upgrade from one representation level to another is valid (progressively more detailed).
    /// </summary>
    /// <param name="from">The source representation level.</param>
    /// <param name="to">The target representation level.</param>
    /// <returns>True if the upgrade is valid, false otherwise.</returns>
    public static bool IsValidUpgrade(RepresentationLevel from, RepresentationLevel to)
    {
        if (from == to)
            return false;

        return (from, to) switch
        {
            // Valid progressions
            (RepresentationLevel.Minimal, RepresentationLevel.Compact) => true,
            (RepresentationLevel.Minimal, RepresentationLevel.Standard) => true,
            (RepresentationLevel.Minimal, RepresentationLevel.Rich) => true,
            (RepresentationLevel.Compact, RepresentationLevel.Standard) => true,
            (RepresentationLevel.Compact, RepresentationLevel.Rich) => true,
            (RepresentationLevel.Standard, RepresentationLevel.Rich) => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets the next representation level in the detail progression.
    /// </summary>
    /// <param name="current">The current representation level.</param>
    /// <returns>The next representation level, or null if already at maximum.</returns>
    public static RepresentationLevel? GetNextLevel(RepresentationLevel current)
    {
        return current switch
        {
            RepresentationLevel.Minimal => RepresentationLevel.Compact,
            RepresentationLevel.Compact => RepresentationLevel.Standard,
            RepresentationLevel.Standard => RepresentationLevel.Rich,
            RepresentationLevel.Rich => null,
            _ => throw new ArgumentOutOfRangeException(nameof(current), current, "Unknown representation level")
        };
    }

    /// <summary>
    /// Gets all representation levels in order from least to most detailed.
    /// </summary>
    /// <returns>An enumerable of representation levels in order.</returns>
    public static IEnumerable<RepresentationLevel> GetLevelProgression()
    {
        yield return RepresentationLevel.Minimal;
        yield return RepresentationLevel.Compact;
        yield return RepresentationLevel.Standard;
        yield return RepresentationLevel.Rich;
    }
}
