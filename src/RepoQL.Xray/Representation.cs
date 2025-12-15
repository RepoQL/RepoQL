namespace RepoQL.Xray;

/// <summary>
/// The level of detail for rendering a result.
/// Each level includes progressively more information.
/// </summary>
public enum Representation
{
    /// <summary>
    /// Headline only (no URI). For wide Explore results. ~5-20 tokens.
    /// </summary>
    Minimal,

    /// <summary>
    /// URI + headline. ~20-80 tokens.
    /// </summary>
    Compact,

    /// <summary>
    /// URI + headline + structure. ~70-280 tokens.
    /// </summary>
    Standard,

    /// <summary>
    /// URI + snippet (no headline - snippet IS the content). ~60-530 tokens.
    /// </summary>
    Rich
}
