namespace RepoQL.Xray;

/// <summary>
/// A decision about how to render a single result.
/// Produced by the decision layer, consumed by the formatting layer.
/// </summary>
/// <param name="Result">The result to render.</param>
/// <param name="Level">The representation level to use.</param>
/// <param name="EstimatedTokens">Estimated token cost at this representation level.</param>
public record RenderingDecision(
    XrayResult Result,
    Representation Level,
    int EstimatedTokens
);
