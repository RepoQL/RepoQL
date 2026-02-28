namespace RepoQL.Explore;

/// <summary>
/// A decision about how to render a single result.
/// Produced by the decision layer, consumed by the formatting layer.
/// </summary>
/// <param name="Result">The result to render.</param>
/// <param name="Level">The representation level to use.</param>
/// <param name="EstimatedTokens">Estimated token cost at this representation level.</param>
/// <param name="ChildDecisions">Nested decisions for child objects (if any).</param>
/// <param name="OmittedChildrenCount">Number of child objects omitted due to relevance filtering.</param>
public record RenderingDecision(
    ExploreResult Result,
    Representation Level,
    int EstimatedTokens,
    IReadOnlyList<RenderingDecision>? ChildDecisions = null,
    int OmittedChildrenCount = 0
);

/// <summary>
/// Result of the decision process.
/// </summary>
/// <param name="Decisions">The rendering decisions for included items.</param>
/// <param name="OmittedCount">Number of results omitted.</param>
/// <param name="OmittedByType">Omitted items grouped by semantic type (e.g., "markdown.doc" → 25).</param>
public record DecisionResult(
    IReadOnlyList<RenderingDecision> Decisions,
    int OmittedCount,
    IReadOnlyDictionary<string, int>? OmittedByType
);
