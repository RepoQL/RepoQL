namespace RepoQL.Rendering;

/// <summary>
/// Context for a rendering operation.
/// </summary>
/// <param name="Intent">The agent's intent (Explore, Find, Read).</param>
/// <param name="TokenBudget">Maximum tokens to use. Required.</param>
/// <param name="Limit">Maximum items to show. Optional - calculated if omitted.</param>
/// <param name="HasSearchCriteria">True if question or patterns were provided. Affects confidence display.</param>
/// <param name="IndexerStatus">Current indexer status for data completeness context. Optional.</param>
public record RenderingContext(
    Intent Intent,
    int TokenBudget,
    int? Limit,
    bool HasSearchCriteria,
    IndexerStatus? IndexerStatus = null
);
