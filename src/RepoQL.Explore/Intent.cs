namespace RepoQL.Explore;

/// <summary>
/// The agent's intent for the explore operation.
/// Determines output shape preferences, not search behavior.
/// </summary>
public enum Intent
{
    /// <summary>
    /// Map territory. Breadth over depth.
    /// Show as much as possible so the agent knows what exists.
    /// </summary>
    Inventory,

    /// <summary>
    /// Locate specific things. Adapts to distribution.
    /// Standouts get rich treatment; no standouts means show inventory for refinement.
    /// </summary>
    Locate,

    /// <summary>
    /// I know what I'm looking for - show me the code and context across relevant files.
    /// Depth over breadth. Fewer items with rich snippets and structure.
    /// </summary>
    Inspect,

    /// <summary>
    /// Synthesize understanding. Runs high-budget explore then summarizes via LLM.
    /// Returns prose explanation with citations, not structured results.
    /// Keywords become the question for LLM synthesis.
    /// </summary>
    Explain
}
