using Microsoft.Extensions.DependencyInjection;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.ConsoleApp.Resources;
using RepoQL.ConsoleApp.Tools;
using RepoQL.Templating;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Helpers;

internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP client-side services. Does NOT register search services or ExploreOrchestrator
    /// (those are registered in ServeCommands for the server-side only).
    /// </summary>
    public static IServiceCollection AddRepoQlConsoleServices(this IServiceCollection services, bool prewarmClient = true)
    {
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
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
        services.AddSingleton<DiagnosticsCollector>();
        services.AddSingleton<SelfTestRunner>();

        // Session orientation nudge
        services.AddSingleton<SessionOrientation>();

        // Command framework
        services.AddSingleton<CommandRegistry>();

        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(ServiceCollectionExtensions).Assembly,
            resourceRoot: "RepoQL.ConsoleApp.Templates");
        return services;
    }
}
