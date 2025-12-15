namespace RepoQL.Xray;

/// <summary>
/// Default implementation of the xray rendering engine.
/// </summary>
public sealed class XrayRenderingEngine : IXrayRenderingEngine
{
    /// <inheritdoc />
    public string Render(IReadOnlyList<XrayResult> results, RenderingContext context)
    {
        var decisionResult = DecisionEngine.Decide(results, context);
        return OutputComposer.Compose(decisionResult, context.HasSearchCriteria, context.IndexerStatus);
    }
}
