namespace RepoQL.Explore;

/// <summary>
/// Input parameters for an explore operation.
/// Pattern strings are raw comma-separated values; the orchestrator parses them.
/// </summary>
/// <param name="TokenBudget">Explicit token budget. Ignored when BudgetTier is set.</param>
/// <param name="BudgetTier">Named budget tier: "low", "medium", "high". System picks within
/// the tier's range based on result quality. When set, TokenBudget is ignored.</param>
/// <param name="Breadth">Breadth 1-10 (default 5). 1 = maximum depth, 10 = maximum breadth.</param>
/// <param name="AutoBreadth">Whether breadth and top-level allocation limit should be resolved from result distribution.</param>
/// <param name="Scope">Optional scope filter (glob pattern or URI).</param>
/// <param name="Keywords">Optional search keywords for semantic search.</param>
/// <param name="Boost">Optional comma-separated regex patterns to boost matches.</param>
/// <param name="Penalize">Optional comma-separated regex patterns to de-rank matches.</param>
/// <param name="Limit">Optional max results to show (null = auto-calculate).</param>
/// <param name="Question">Optional natural language question for reranking. Keywords drive retrieval; question drives reranking.</param>
public sealed record ExploreQuery(
    int TokenBudget,
    string? BudgetTier = null,
    int Breadth = 5,
    bool AutoBreadth = false,
    string? Scope = null,
    string? Keywords = null,
    string? Boost = null,
    string? Penalize = null,
    int? Limit = null,
    string? Question = null);
