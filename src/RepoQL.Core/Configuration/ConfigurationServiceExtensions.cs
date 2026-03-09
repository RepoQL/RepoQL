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
        ILogger? logger = null)
    {
        var registry = SettingRegistry.Build();
        var resolved = ConfigurationLoader.Load(registry, repoRoot, logger);

        services.AddSingleton(registry);
        services.AddSingleton(resolved);

        // Register RepoQlConfig as a factory so it always returns the current instance,
        // even after Reload() is called.
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Settings);

        // Watch config files on disk and auto-reload on changes.
        // Created eagerly so watchers start immediately.
        var watcher = new ConfigFileWatcher(resolved, logger);
        services.AddSingleton(watcher);

        return services;
    }
}
