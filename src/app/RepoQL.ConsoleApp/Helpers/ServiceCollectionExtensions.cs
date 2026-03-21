using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Auth;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Feedback;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.ConsoleApp.Resources;
using RepoQL.ConsoleApp.Tools;
using RepoQL.Contracts;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Inference;
using RepoQL.Core.Cloud;
using RepoQL.Core.Configuration;
using RepoQL.Sandbox;
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
            .AddCloudAuthServices()
            .AddSandboxServices()
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
            .AddConfigurationServices(startupRepoRoot, ConfigReloadMode.Poll)
            .AddCloudAuthServices()
            .AddInferenceServices()
            .AddSandboxServices()
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
        services.AddSingleton<SessionInfo>();
        services.AddSingleton<FeedbackStore>();
        if (includeSessionOrientation)
            services.AddSingleton<SessionOrientation>();

        return services;
    }

    private static IServiceCollection AddConfigurationServices(
        this IServiceCollection services,
        string startupRepoRoot,
        ConfigReloadMode reloadMode = ConfigReloadMode.Watch)
    {
        services.AddResolvedConfig(startupRepoRoot, reloadMode: reloadMode);
        services.AddScoped<EnvironmentContext>();
        services.AddSingleton<IModuleRegistry>(_ => new FileBasedModuleRegistry(startupRepoRoot));
        return services;
    }

    private static IServiceCollection AddInferenceServices(this IServiceCollection services)
    {
        services.AddCloudCredentialProvider();

        services.AddSingleton<IInferenceProvider>(sp =>
        {
            var config = sp.GetRequiredService<RepoQlConfig>();
            var settings = config.Inference;
            var credentialProvider = sp.GetService<ICloudCredentialProvider>();
            if (string.IsNullOrWhiteSpace(settings.ServiceUrl) ||
                credentialProvider is null)
                return new DisabledInferenceProvider();

            return new RepoQL.Inference.Client.InferenceClient(
                settings.ServiceUrl,
                credentialProvider,
                sp.GetService<ILogger<RepoQL.Inference.Client.InferenceClient>>());
        });

        return services;
    }

    private static IServiceCollection AddCloudAuthServices(this IServiceCollection services)
    {
        services.AddSingleton<CloudAuthSessionStore>();
        services.AddSingleton<CloudAuthService>();
        return services;
    }

    private static IServiceCollection AddSandboxServices(this IServiceCollection services)
    {
        // WASM sandbox — eagerly construct so failures are caught at startup, not at resolve time.
        // If it fails (missing native lib, missing .wasm), the execute tool returns "unavailable".
        try
        {
            var sandbox = new WasmtimeSandbox();
            services.AddSingleton<IWasmSandbox>(sandbox);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WASM sandbox unavailable: {ex.Message}");
        }

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
