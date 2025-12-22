using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.ConsoleApp.Resources;
using RepoQL.Templating;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Helpers;

internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP client-side services. Does NOT register search services or XrayOrchestrator
    /// (those are registered in ServeCommands for the server-side only).
    /// </summary>
    public static IServiceCollection AddRepoQlConsoleServices(this IServiceCollection services, bool prewarmClient = true)
    {
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        services.AddSingleton<IResultFormatter, UnstructuredFormatter>();
        services.AddSingleton<IResultFormatter, JsonLDFormatter>();
        services.AddSingleton<IResultFormatter, ToonFormatter>();
        services.AddSingleton<ResultFormatterFactory>();
        services.AddSingleton<RepoQlClientProvider>();
        if (prewarmClient)
        {
            services.AddHostedService<RepoQlClientWarmupService>();
        }
        services.AddSingleton<QueryExecutor>();
        services.AddSingleton<RepoResourceService>();

        // Diagnostics
        services.AddSingleton<SelfTestRunner>();

        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(ServiceCollectionExtensions).Assembly,
            resourceRoot: "RepoQL.ConsoleApp.Templates");
        return services;
    }
}
