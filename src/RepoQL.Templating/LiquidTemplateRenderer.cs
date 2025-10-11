using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;
using Microsoft.Extensions.FileProviders;

namespace RepoQL.Templating;

/// <summary>
/// Fluid.NET based template renderer that loads templates from embedded resources using an <see cref="IFileProvider"/>.
/// - Supports strongly-typed objects or dictionaries as the model.
/// - Registers model types with the Fluid member access strategy at runtime so public properties are available to templates.
/// - Caches parsed templates by path for performance.
/// - Honors Fluid includes ({% include %}) via the configured <see cref="TemplateOptions.FileProvider"/>.
///
/// Usage:
///   1) Add Liquid templates to your project, mark them as EmbeddedResource.
///   2) Choose a resource root (e.g., "My.Assembly.Namespace.Templates").
///   3) new LiquidTemplateRenderer(typeof(SomeTypeInYourAssembly).Assembly, "My.Assembly.Namespace.Templates").
///   4) await renderer.RenderAsync("xray/headline", new { Name = "AuthService", Methods = 5 });
/// </summary>
public sealed class LiquidTemplateRenderer : ITemplateRenderer
{
    private readonly IFileProvider _fileProvider;
    private readonly TemplateOptions _options;
    private readonly FluidParser _parser = new();
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultExtension;
    private readonly HtmlEncoder? _defaultEncoder;

    /// <summary>
    /// Create a renderer using an embedded file provider rooted at the given resource namespace.
    /// </summary>
    /// <param name="assembly">Assembly containing embedded templates.</param>
    /// <param name="resourceRoot">Root namespace for templates (e.g., "My.Assembly.Namespace.Templates"). May be null to use assembly default.</param>
    /// <param name="defaultExtension">Default extension appended when none supplied, defaults to ".liquid".</param>
    /// <param name="configure">Optional hook to customize <see cref="TemplateOptions"/> (filters, culture, etc).</param>
    /// <param name="defaultEncoder">Optional encoder for HTML/XML encoding of template output.</param>
    public LiquidTemplateRenderer(Assembly assembly, string? resourceRoot = null, string defaultExtension = ".liquid", Action<TemplateOptions>? configure = null, HtmlEncoder? defaultEncoder = null)
        : this(new EmbeddedFileProvider(assembly, resourceRoot ?? string.Empty), defaultExtension, configure, defaultEncoder)
    {
    }

    /// <summary>
    /// Create a renderer using a custom file provider (embedded, physical, composite, etc.).
    /// </summary>
    public LiquidTemplateRenderer(IFileProvider fileProvider, string defaultExtension = ".liquid", Action<TemplateOptions>? configure = null, HtmlEncoder? defaultEncoder = null)
    {
        _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        _defaultExtension = string.IsNullOrWhiteSpace(defaultExtension) ? ".liquid" : defaultExtension;

        _options = new TemplateOptions
        {
            // Fluid resolves {% include %} using the configured file provider
            FileProvider = _fileProvider
        };

        // Basic, broadly-useful filters could be registered here in the future.
        configure?.Invoke(_options);
        _defaultEncoder = defaultEncoder;
    }

    /// <summary>
    /// Create a renderer using an existing TemplateOptions instance (shared and preconfigured via DI).
    /// The provided options' PartialProvider will be overridden to use the specified file provider.
    /// </summary>
    public LiquidTemplateRenderer(IFileProvider fileProvider, TemplateOptions options, string defaultExtension = ".liquid", HtmlEncoder? defaultEncoder = null)
    {
        _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        _defaultExtension = string.IsNullOrWhiteSpace(defaultExtension) ? ".liquid" : defaultExtension;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Ensure includes resolve against the provided file provider
        _options.FileProvider = _fileProvider;
        _defaultEncoder = defaultEncoder;
    }

    public async Task<string> RenderAsync(string templateNameOrPath, object? model, CancellationToken cancellationToken = default)
    {
        if (model is IReadOnlyDictionary<string, object?> dict)
            return await RenderAsync(templateNameOrPath, dict, cancellationToken);

        var context = new TemplateContext(options: _options);

        if (model is not null)
        {
            // Dynamically allow public property access for the model type.
            context.Options.MemberAccessStrategy.Register(model.GetType());
            context.SetValue("model", FluidValue.Create(model, _options));
        }
        

        var template = await GetOrParseTemplateAsync(templateNameOrPath, cancellationToken);
        if (_defaultEncoder is not null)
            return await template.RenderAsync(context, _defaultEncoder);
        return await template.RenderAsync(context);
    }

    public async Task<string> RenderAsync(string templateNameOrPath, IReadOnlyDictionary<string, object?> model, CancellationToken cancellationToken = default)
    {
        var context = new TemplateContext(options: _options);
        foreach (var (k, v) in model)
        {
            context.SetValue(k, FluidValue.Create(v, _options));
        }

        var template = await GetOrParseTemplateAsync(templateNameOrPath, cancellationToken);
        if (_defaultEncoder is not null)
            return await template.RenderAsync(context, _defaultEncoder);
        return await template.RenderAsync(context);
    }

    private async Task<IFluidTemplate> GetOrParseTemplateAsync(string nameOrPath, CancellationToken ct)
    {
        var path = NormalizePath(nameOrPath);
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        var file = _fileProvider.GetFileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException($"Template not found: {path}");

        string content;
        await using (var stream = file.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            content = (await reader.ReadToEndAsync(ct)).Trim();
        }

        if (!_parser.TryParse(content, out var tpl, out var errors))
            throw new InvalidOperationException($"Failed to parse template '{path}': {string.Join("; ", errors)}");

        _cache[path] = tpl;
        return tpl;
    }

    private string NormalizePath(string nameOrPath)
    {
        var p = nameOrPath.Replace('\\', '/');
        if (p.StartsWith('/'))
            p = p[1..];
        if (!p.EndsWith(_defaultExtension, StringComparison.OrdinalIgnoreCase))
            p += _defaultExtension;
        return p;
    }
}
