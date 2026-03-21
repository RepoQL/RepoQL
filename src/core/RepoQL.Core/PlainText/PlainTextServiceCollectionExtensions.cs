using Microsoft.Extensions.DependencyInjection;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Core.PlainText;

public static class PlainTextServiceCollectionExtensions
{
    public static IServiceCollection AddPlainTextFormat(this IServiceCollection services)
    {
        services.AddIndexingProcessor<PlainTextParser>();
        return services;
    }
}
