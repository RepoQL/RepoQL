using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Formats.Json.Analysis;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Json;

/// <summary>
/// DI registration extensions for JSON format support.
///
/// Purpose: Registers loader, schema provider, descriptor, and pipeline processors for generic JSON indexing.
///
/// Complexity: Standard service registration wiring.
/// </summary>
public static class JsonServiceCollectionExtensions
{
    public static IServiceCollection AddJsonFormat(this IServiceCollection services)
    {
        services.AddSingleton<JsonStructureParser>();
        services.AddSingleton<JsonLoader>();
        services.AddSingleton<JsonSecretDetector>();
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<JsonLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<JsonLoader>();
            return new FormatDescriptor(
                JsonMediaTypes.Json,
                loader,
                sp.GetRequiredService<JsonSecretDetector>(),
                loader,
                ["json", "jsonc", "json5", "jsonl", "ndjson"]);
        });

        services.AddIndexingProcessor<JsonClassifier>();
        services.AddIndexingProcessor<JsonParser>();

        return services;
    }
}
