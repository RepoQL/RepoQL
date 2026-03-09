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
    public static IServiceCollection AddRepoQlCliServices(this IServiceCollection services)
    {
        var startupRepoRoot = RepoLocator.FindRepoRoot();

        services
            .AddConsoleSurfaceServices()
            .AddRepoQlClientServices(prewarmClient: false)
            .AddFormattingServices()
            .AddDiagnosticsServices(includeSessionOrientation: false)
            .AddConfigurationServices(startupRepoRoot)
            .AddCommandServices()
            .AddLiquidTemplateServices();

        return services;
    }

    public static IServiceCollection AddRepoQlMcpServices(this IServiceCollection services)
    {
        var startupRepoRoot = RepoLocator.FindRepoRoot();

        services
            .AddConsoleSurfaceServices()
            .AddRepoQlClientServices(prewarmClient: true)
            .AddFormattingServices()
            .AddDiagnosticsServices(includeSessionOrientation: true)
            .AddConfigurationServices(startupRepoRoot)
            .AddLlmServices()
            .AddCommandServices()
            .AddResourceServices()
            .AddLiquidTemplateServices();

        return services;
    }

    private static IServiceCollection AddConsoleSurfaceServices(this IServiceCollection services)
    {
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        return services;
    }

    private static IServiceCollection AddRepoQlClientServices(this IServiceCollection services, bool prewarmClient)
    {
        services.AddSingleton<RepoQlClientProvider>();
        if (prewarmClient)
            services.AddHostedService<RepoQlClientWarmupService>();

        return services;
    }

    private static IServiceCollection AddFormattingServices(this IServiceCollection services)
    {
        services.AddSingleton<IResultFormatter, JsonLDFormatter>();
        services.AddSingleton<IResultFormatter, ToonFormatter>();
        services.AddSingleton<ResultFormatterFactory>();
        services.AddSingleton<QueryExecutor>();
        return services;
    }

    private static IServiceCollection AddDiagnosticsServices(this IServiceCollection services, bool includeSessionOrientation)
    {
        services.AddSingleton<DiagnosticsCollector>();
        services.AddSingleton<SelfTestRunner>();
        if (includeSessionOrientation)
            services.AddSingleton<SessionOrientation>();

        return services;
    }

    private static IServiceCollection AddConfigurationServices(this IServiceCollection services, string startupRepoRoot)
    {
        services.AddResolvedConfig(startupRepoRoot);
        services.AddScoped<EnvironmentContext>();
        return services;
    }

    private static IServiceCollection AddLlmServices(this IServiceCollection services)
    {
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

        return services;
    }

    private static IServiceCollection AddCommandServices(this IServiceCollection services)
    {
        services.AddSingleton<CommandRegistry>();
        return services;
    }

    private static IServiceCollection AddResourceServices(this IServiceCollection services)
    {
        services.AddSingleton<RepoResourceService>();
        return services;
    }

    private static IServiceCollection AddLiquidTemplateServices(this IServiceCollection services)
    {
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(ServiceCollectionExtensions).Assembly,
            resourceRoot: "RepoQL.ConsoleApp.Templates");
        return services;
    }
}

