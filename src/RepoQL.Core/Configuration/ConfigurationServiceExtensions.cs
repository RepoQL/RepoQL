using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Core.Configuration;

/// <summary>
/// DI registration for the centralized configuration system.
/// </summary>
public static class ConfigurationServiceExtensions
{
    /// <summary>
    /// Registers <see cref="SettingRegistry"/>, <see cref="ResolvedConfig"/>, and <see cref="RepoQlConfig"/>
    /// as singletons. Call before <c>AddRepoIndexer()</c>.
    /// </summary>
    public static IServiceCollection AddResolvedConfig(
        this IServiceCollection services,
        string? repoRoot,
        ILogger? logger = null,
        ConfigReloadMode reloadMode = ConfigReloadMode.Watch)
    {
        var registry = SettingRegistry.Build();
        var resolved = ConfigurationLoader.Load(registry, repoRoot, logger);

        services.AddSingleton(registry);
        services.AddSingleton(resolved);

        // Register RepoQlConfig as a factory so it always returns the current instance,
        // even after Reload() is called.
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Settings);

        switch (reloadMode)
        {
            case ConfigReloadMode.Watch:
                services.AddSingleton(new ConfigFileWatcher(resolved, logger));
                break;
            case ConfigReloadMode.Poll:
                services.AddSingleton(new ConfigFilePoller(resolved, logger));
                break;
            case ConfigReloadMode.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reloadMode), reloadMode, null);
        }

        return services;
    }
}
