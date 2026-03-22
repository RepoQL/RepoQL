using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Hosting;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;

namespace RepoQL.Formats.Go;

public static class GoServiceCollectionExtensions
{
    public static IServiceCollection AddGoFormat(this IServiceCollection services)
    {
        services.AddSingleton<GoLoader>(sp => new GoLoader(
            logger: sp.GetService<ILogger<GoLoader>>()));
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<GoLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<GoLoader>();
            return new FormatDescriptor(
                GoMediaTypes.Go,
                loader,
                analyzer: null!,
                loader,
                new[] { "go" });
        });

        services.AddIndexingProcessor<GoClassifier>();
        services.AddIndexingProcessor<GoParser>();
        services.AddIndexingProcessor<GoInterfaceSatisfactionAnalyzer>(default(IAsyncPipeline<IAnnotatedArtifact, Annotation[]>));

        return services;
    }
}
