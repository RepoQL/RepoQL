using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Formats.CSS;

public static class CSSServiceCollectionExtensions
{
    public static IServiceCollection AddCSSFormat(this IServiceCollection services)
    {
        // Register loader
        services.AddSingleton<CSSLoader>();

        // Register parser pipeline
        services.AddSingleton<IAsyncPipeline<IClassifiedArtifact, Records?>, CSSParser>();

        return services;
    }
}
