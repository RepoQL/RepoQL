using Microsoft.Extensions.DependencyInjection;

namespace RepoQL.Contracts;

/// <summary>
/// Extension methods for registering UriRegistry in dependency injection.
/// </summary>
public static class UriRegistryServiceExtensions
{
    /// <summary>
    /// Registers UriRegistry as a singleton service.
    /// </summary>
    public static IServiceCollection AddUriRegistry(this IServiceCollection services)
    {
        services.AddSingleton<UriRegistry>();
        return services;
    }
}
