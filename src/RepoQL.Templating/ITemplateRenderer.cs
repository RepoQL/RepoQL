namespace RepoQL.Templating;

/// <summary>
/// Renders embedded templates with a provided model.
/// </summary>
public interface ITemplateRenderer
{
    /// <summary>
    /// Render a template by name/path using the supplied model. The template is looked up
    /// via the configured file provider (typically an EmbeddedFileProvider).
    /// </summary>
    /// <param name="templateNameOrPath">Logical template name or relative path. Extension is optional.</param>
    /// <param name="model">Anonymous object or dictionary. For objects, public properties are exposed to the template as <c>model</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered template output.</returns>
    Task<string> RenderAsync(string templateNameOrPath, object? model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Render a template by name/path using the supplied dictionary model. Keys become top-level variables.
    /// </summary>
    Task<string> RenderAsync(string templateNameOrPath, IReadOnlyDictionary<string, object?> model, CancellationToken cancellationToken = default);
}