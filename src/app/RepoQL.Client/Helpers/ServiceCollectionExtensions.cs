using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Commands;
using RepoQL.Client.Auth;
using RepoQL.Client.Diagnostics;
using RepoQL.Client.Feedback;
using RepoQL.Client.Formatters;
using RepoQL.Client.Tools;
using RepoQL.Contracts;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Inference;
using RepoQL.Core.Cloud;
using RepoQL.Core.Configuration;
using RepoQL.Sandbox;
using RepoQL.Templating;
using Spectre.Console;

namespace RepoQL.Client.Helpers;

/// <summary>
/// Shared service registration methods used by both MCP and CLI modes.
/// </summary>
public static class ClientServiceCollectionExtensions
{
    public static IServiceCollection AddConsoleSurfaceServices(this IServiceCollection services)
    {
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
        return services;
    }

    public static IServiceCollection AddRepoQlClientServices(this IServiceCollection services, bool prewarmClient)
    {
        services.AddSingleton<RepoQlClientProvider>();
        if (prewarmClient)
            services.AddHostedService<RepoQlClientWarmupService>();

        return services;
    }

    public static IServiceCollection AddFormattingServices(this IServiceCollection services)
    {
        services.AddSingleton<IResultFormatter, JsonLDFormatter>();
        services.AddSingleton<IResultFormatter, ToonFormatter>();
        services.AddSingleton<ResultFormatterFactory>();
        services.AddSingleton<QueryExecutor>();
        return services;
    }

    public static IServiceCollection AddDiagnosticsServices(this IServiceCollection services)
    {
        services.AddSingleton<DiagnosticsCollector>();
        services.AddSingleton<SelfTestRunner>();
        services.AddSingleton<SessionInfo>();
        services.AddSingleton<FeedbackStore>();
        return services;
    }

    public static IServiceCollection AddConfigurationServices(
        this IServiceCollection services,
        string startupRepoRoot,
        ConfigReloadMode reloadMode = ConfigReloadMode.Watch)
    {
        services.AddResolvedConfig(startupRepoRoot, reloadMode: reloadMode);
        services.AddScoped<EnvironmentContext>();
        services.AddSingleton<IModuleRegistry>(_ => new FileBasedModuleRegistry(startupRepoRoot));
        return services;
    }

    public static IServiceCollection AddInferenceServices(this IServiceCollection services)
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

    public static IServiceCollection AddCloudAuthServices(this IServiceCollection services)
    {
        services.AddCloudCredentialProvider();
        services.AddSingleton<CloudAuthService>();
        return services;
    }

    public static IServiceCollection AddSandboxServices(this IServiceCollection services)
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

    public static IServiceCollection AddCommandServices(this IServiceCollection services)
    {
        services.AddSingleton<CommandRegistry>();
        return services;
    }

    public static IServiceCollection AddLiquidTemplateServices(this IServiceCollection services)
    {
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(ClientServiceCollectionExtensions).Assembly,
            resourceRoot: "RepoQL.Client.Templates");
        return services;
    }
}
