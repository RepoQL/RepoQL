namespace RepoQL.Xray;

/// <summary>
/// Input parameters for an xray operation.
/// Pattern strings are raw comma-separated values; the orchestrator parses them.
/// </summary>
/// <param name="TokenBudget">Maximum tokens to invest in the response.</param>
/// <param name="Intent">The search intent (Explore, Find, Examine).</param>
/// <param name="Scope">Optional scope filter (glob pattern or URI).</param>
/// <param name="Keywords">Optional search keywords for semantic search.</param>
/// <param name="Boost">Optional comma-separated regex patterns to boost matches.</param>
/// <param name="Penalize">Optional comma-separated regex patterns to de-rank matches.</param>
/// <param name="Limit">Optional max results to show (null = auto-calculate).</param>
public sealed record XrayQuery(
    int TokenBudget,
    Intent Intent,
    string? Scope,
    string? Keywords,
    string? Boost,
    string? Penalize,
    int? Limit);
