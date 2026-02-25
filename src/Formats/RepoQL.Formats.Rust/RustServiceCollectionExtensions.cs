using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Rust;

public static class RustServiceCollectionExtensions
{
    public static IServiceCollection AddRustFormat(this IServiceCollection services)
    {
        services.AddSingleton<RustLoader>(sp => new RustLoader(
            logger: sp.GetService<ILogger<RustLoader>>()));
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<RustLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<RustLoader>();
            return new FormatDescriptor(
                RustMediaTypes.Rust,
                loader,
                analyzer: null!,
                loader,
                new[] { "rs", "build.rs" });
        });

        services.AddIndexingProcessor<RustClassifier>();
        services.AddIndexingProcessor<RustParser>();

        return services;
    }
}
