using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Client.Helpers;

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
            .AddDiagnosticsServices()
            .AddConfigurationServices(startupRepoRoot)
            .AddCloudAuthServices()
            .AddSandboxServices()
            .AddCommandServices()
            .AddLiquidTemplateServices();

        return services;
    }
}
