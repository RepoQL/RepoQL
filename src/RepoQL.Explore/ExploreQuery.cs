namespace RepoQL.Explore;

/// <summary>
/// Input parameters for an explore operation.
/// Pattern strings are raw comma-separated values; the orchestrator parses them.
/// </summary>
/// <param name="TokenBudget">Maximum tokens to invest in the response.</param>
/// <param name="Breadth">Breadth 1-10 (default 5). 1 = maximum depth, 10 = maximum breadth.</param>
/// <param name="Scope">Optional scope filter (glob pattern or URI).</param>
/// <param name="Keywords">Optional search keywords for semantic search.</param>
/// <param name="Boost">Optional comma-separated regex patterns to boost matches.</param>
/// <param name="Penalize">Optional comma-separated regex patterns to de-rank matches.</param>
/// <param name="Limit">Optional max results to show (null = auto-calculate).</param>
public sealed record ExploreQuery(
    int TokenBudget,
    int Breadth = 5,
    string? Scope = null,
    string? Keywords = null,
    string? Boost = null,
    string? Penalize = null,
    int? Limit = null);
