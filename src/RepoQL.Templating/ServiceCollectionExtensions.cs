using System.Reflection;
using System.Text.Encodings.Web;
using Fluid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using RepoQL.Templating.Filters;

namespace RepoQL.Templating;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a Liquid templating stack using an <see cref="IFileProvider"/> as the template root.
    /// Configures a shared <see cref="TemplateOptions"/>, registers standard filters, optional member access registrations,
    /// and a singleton <see cref="ITemplateRenderer"/>.
    /// </summary>
    public static IServiceCollection AddLiquidTemplating(
        this IServiceCollection services,
        IFileProvider templates,
        IEnumerable<Type>? memberTypes = null,
        Action<TemplateOptions>? configureOptions = null,
        HtmlEncoder? defaultEncoder = null,
        string defaultExtension = ".liquid")
    {
        if (templates is null) throw new ArgumentNullException(nameof(templates));

        // Build a reusable TemplateOptions instance
        var options = new TemplateOptions
        {
            CultureInfo = System.Globalization.CultureInfo.InvariantCulture,
            TimeZone = TimeZoneInfo.Utc,
            FileProvider = templates
        };

        // Register standard filters
        StandardFilters.RegisterAll(options);

        // Member allow-list: register specified types (optional)
        if (memberTypes is not null)
        {
            foreach (var t in memberTypes)
            {
                options.MemberAccessStrategy.Register(t);
            }
        }

        // Caller customization hook
        configureOptions?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<ITemplateRenderer>(_ => new LiquidTemplateRenderer(templates, options, defaultExtension, defaultEncoder));
        return services;
    }

    /// <summary>
    /// Register Liquid templating with templates pulled from embedded resources under the specified resource root.
    /// </summary>
    public static IServiceCollection AddLiquidTemplatingFromEmbedded(
        this IServiceCollection services,
        Assembly assembly,
        string? resourceRoot = null,
        IEnumerable<Type>? memberTypes = null,
        Action<TemplateOptions>? configureOptions = null,
        HtmlEncoder? defaultEncoder = null,
        string defaultExtension = ".liquid")
    {
        var provider = new EmbeddedFileProvider(assembly, resourceRoot ?? string.Empty);
        return services.AddLiquidTemplating(provider, memberTypes, configureOptions, defaultEncoder, defaultExtension);
    }
}
