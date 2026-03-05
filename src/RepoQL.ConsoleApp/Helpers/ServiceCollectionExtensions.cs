using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.ConsoleApp.Resources;
using RepoQL.ConsoleApp.Tools;
using RepoQL.Contracts;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;
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
        var startupRepoRoot = RepoLocator.FindRepoRoot();

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

        // Configuration + settings metadata for ::config commands
        services.AddResolvedConfig(startupRepoRoot);
        services.AddScoped<EnvironmentContext>();

        // LLM provider for explain tool synthesis
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var config = sp.GetRequiredService<RepoQlConfig>();
            var apiKey = config.Llm.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                return new DisabledLlmProvider();

            return new LLM.Client.OpenRouterLlmProvider(
                apiKey: apiKey,
                settings: config.Llm,
                logger: sp.GetService<ILogger<LLM.Client.OpenRouterLlmProvider>>());
        });

        // Command framework
        services.AddSingleton<CommandRegistry>();

        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(ServiceCollectionExtensions).Assembly,
            resourceRoot: "RepoQL.ConsoleApp.Templates");
        return services;
    }
}
