namespace RepoQL.Rendering;

/// <summary>
/// The agent's intent for the xray operation.
/// Determines output shape preferences, not search behavior.
/// </summary>
public enum Intent
{
    /// <summary>
    /// Map territory. Breadth over depth.
    /// Show as much as possible so the agent knows what exists.
    /// </summary>
    Explore,

    /// <summary>
    /// Locate specific things. Adapts to distribution.
    /// Standouts get rich treatment; no standouts means show inventory for refinement.
    /// </summary>
    Find,

    /// <summary>
    /// I know what I'm looking for - show me the code and context across relevant files.
    /// Depth over breadth. Fewer items with rich snippets and structure.
    /// </summary>
    Examine
}
