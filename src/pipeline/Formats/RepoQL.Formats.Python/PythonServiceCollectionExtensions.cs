using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Python;

public static class PythonServiceCollectionExtensions
{
    public static IServiceCollection AddPythonFormat(this IServiceCollection services)
    {
        services.AddSingleton<PythonLoader>(sp => new PythonLoader(
            logger: sp.GetService<ILogger<PythonLoader>>()));
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<PythonLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<PythonLoader>();
            return new FormatDescriptor(
                PythonMediaTypes.Python,
                loader,
                analyzer: null!,
                loader,
                new[] { "py", "pyw", "pyi" });
        });

        services.AddIndexingProcessor<PythonClassifier>();
        services.AddIndexingProcessor<PythonParser>();

        return services;
    }
}
