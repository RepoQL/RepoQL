namespace RepoQL.Explore;

/// <summary>
/// Engine for rendering explore results.
/// </summary>
public interface IExploreRenderingEngine
{
    /// <summary>
    /// Render results to a formatted string.
    /// </summary>
    /// <param name="results">The results to render.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>The formatted output string.</returns>
    string Render(IReadOnlyList<ExploreResult> results, RenderingContext context);
}
