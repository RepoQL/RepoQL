namespace RepoQL.Explore;

/// <summary>
/// Default implementation of the explore rendering engine.
/// </summary>
public sealed class ExploreRenderingEngine : IExploreRenderingEngine
{
    /// <inheritdoc />
    public string Render(IReadOnlyList<ExploreResult> results, RenderingContext context)
    {
        var decisionResult = DecisionEngine.Decide(results, context);
        return OutputComposer.Compose(decisionResult, context.HasSearchCriteria, context.IndexerStatus);
    }
}
