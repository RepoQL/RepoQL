using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.ConsoleApp.Resources;
using RepoQL.ConsoleApp.Search;
using RepoQL.Rendering;
using RepoQL.Rendering.Search;
using RepoQL.Templating;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Helpers;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRepoQlConsoleServices(this IServiceCollection services, bool prewarmClient = true)
    {
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        services.AddSingleton<IResultFormatter, UnstructuredFormatter>();
        services.AddSingleton<IResultFormatter, JsonLDFormatter>();
        services.AddSingleton<ResultFormatterFactory>();
        services.AddSingleton<RepoQlClientProvider>();
        if (prewarmClient)
        {
            services.AddHostedService<RepoQlClientWarmupService>();
        }
        services.AddSingleton<QueryExecutor>();
        services.AddSingleton<RepoResourceService>();
        services.AddSingleton<IXrayRenderingEngine, XrayRenderingEngine>();

        // Search services
        services.AddSingleton<IDocumentSearchService, DocumentSearchService>();
        services.AddSingleton<IObjectSearchService, ObjectSearchService>();
        services.AddSingleton<IXraySearchEngine, XraySearchEngine>();
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(ServiceCollectionExtensions).Assembly,
            resourceRoot: "RepoQL.ConsoleApp.Templates");
        return services;
    }
}
