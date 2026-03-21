namespace RepoQL.Explore;

/// <summary>
/// Result of an explore operation, containing both rendered output and structured results.
/// </summary>
/// <param name="RenderedOutput">Pre-rendered markdown output for display.</param>
/// <param name="Results">Structured results for programmatic use.</param>
/// <param name="Truncated">True if results were truncated due to token budget.</param>
public sealed record ExploreExecutionResult(
    string RenderedOutput,
    IReadOnlyList<ExploreResult> Results,
    bool Truncated);
