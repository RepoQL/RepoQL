using Microsoft.Extensions.DependencyInjection;
using RepoQL.McpServer.Resources;
using RepoQL.McpServer.Tools;
using RepoQL.Contracts;
using RepoQL.Client.Helpers;

namespace RepoQL.McpServer.Helpers;

/// <summary>
/// MCP-specific service registrations.
/// </summary>
public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddRepoQlMcpServices(this IServiceCollection services)
    {
        var startupRepoRoot = RepoLocator.FindRepoRoot();

        services
            .AddConsoleSurfaceServices()
            .AddRepoQlClientServices(prewarmClient: true)
            .AddFormattingServices()
            .AddDiagnosticsServices()
            .AddConfigurationServices(startupRepoRoot, Core.Configuration.ConfigReloadMode.Poll)
            .AddCloudAuthServices()
            .AddInferenceServices()
            .AddSandboxServices()
            .AddCommandServices()
            .AddResourceServices()
            .AddLiquidTemplateServices();

        services.AddSingleton<SessionOrientation>();

        return services;
    }

    private static IServiceCollection AddResourceServices(this IServiceCollection services)
    {
        services.AddSingleton<RepoResourceService>();
        return services;
    }
}
