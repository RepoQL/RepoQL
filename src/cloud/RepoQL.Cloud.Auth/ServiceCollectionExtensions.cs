using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RepoQL.Cloud.Auth;

/// <summary>
/// Purpose: Register the shared cloud service auth stack in a single call.
/// Complexity: Binds options, provisions JWKS HTTP access, and wires the interceptor and warmup hosted service.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRepoQlServerAuth(
        this IServiceCollection services,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.Configure<AuthOptions>(section);
        services.AddHttpClient(nameof(AuthValidationService));
        services.AddSingleton<AuthValidationService>();
        services.AddSingleton<AuthInterceptor>();
        services.AddHostedService<JwksWarmupHostedService>();
        return services;
    }
}
