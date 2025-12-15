namespace RepoQL.Xray;

/// <summary>
/// Engine for rendering xray results.
/// </summary>
public interface IXrayRenderingEngine
{
    /// <summary>
    /// Render results to a formatted string.
    /// </summary>
    /// <param name="results">The results to render.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>The formatted output string.</returns>
    string Render(IReadOnlyList<XrayResult> results, RenderingContext context);
}
