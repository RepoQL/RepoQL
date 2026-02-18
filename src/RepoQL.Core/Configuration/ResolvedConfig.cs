using RepoQL.Contracts.Configuration;

namespace RepoQL.Core.Configuration;

/// <summary>
/// Purpose: Wraps a resolved <see cref="RepoQlConfig"/> with provenance information.
/// Complexity: Holds the typed config + a per-key map of where each value came from.
/// Supports reload for <c>::config</c> mutations. Thread-safe for concurrent readers during reload.
/// </summary>
public sealed class ResolvedConfig
{
    private readonly SettingRegistry _registry;
    private readonly Lock _reloadLock = new();
    private volatile Dictionary<string, ResolvedSetting> _resolved;

    public ResolvedConfig(
        RepoQlConfig settings,
        Dictionary<string, ResolvedSetting> resolved,
        SettingRegistry registry,
        string userConfigDir)
    {
        Settings = settings;
        _resolved = resolved;
        _registry = registry;
        UserConfigDir = userConfigDir;
    }

    /// <summary>The typed config values. Components should inject <see cref="ResolvedConfig"/> and use this property.</summary>
    public RepoQlConfig Settings { get; private set; }

    /// <summary>The resolved user configuration directory (normally <c>~/.repoql</c>).</summary>
    public string UserConfigDir { get; private set; }

    /// <summary>All resolved settings with their provenance.</summary>
    public IReadOnlyDictionary<string, ResolvedSetting> AllResolved => _resolved;

    /// <summary>Gets provenance for a single key, or null if the key doesn't exist.</summary>
    public ResolvedSetting? GetProvenance(string key)
        => _resolved.TryGetValue(key, out var setting) ? setting : null;

    /// <summary>
    /// Re-loads configuration from disk and env vars.
    /// Called after <c>::config</c> writes to pick up changes.
    /// Mutates <see cref="Settings"/> in-place so all holders see the update.
    /// </summary>
    public void Reload(string? repoRoot, Microsoft.Extensions.Logging.ILogger? logger = null, string? userConfigDir = null)
    {
        var effectiveUserConfigDir = userConfigDir ?? UserConfigDir;
        var fresh = ConfigurationLoader.Load(_registry, repoRoot, logger, effectiveUserConfigDir);

        lock (_reloadLock)
        {
            // Copy values from fresh config into the existing Settings instance
            // so all injected references see the update.
            foreach (var def in _registry.All)
            {
                var freshSection = def.SectionProperty.GetValue(fresh.Settings);
                var currentSection = def.SectionProperty.GetValue(Settings);
                if (freshSection is null || currentSection is null)
                    continue;
                var value = def.SettingProperty.GetValue(freshSection);
                def.SettingProperty.SetValue(currentSection, value);
            }

            // Atomic swap of the provenance map
            _resolved = new Dictionary<string, ResolvedSetting>(
                fresh.AllResolved, StringComparer.OrdinalIgnoreCase);
            UserConfigDir = effectiveUserConfigDir;
        }
    }
}
