using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.PHP;

public static class PHPServiceCollectionExtensions
{
    public static IServiceCollection AddPHPFormat(this IServiceCollection services)
    {
        services.AddSingleton<PHPLoader>(sp => new PHPLoader(
            logger: sp.GetService<ILogger<PHPLoader>>()));
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<PHPLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<PHPLoader>();
            return new FormatDescriptor(
                PHPMediaTypes.PHP,
                loader,
                analyzer: null,
                loader,
                new[] { "php" });
        });

        services.AddIndexingProcessor<PHPParser>();

        return services;
    }
}
