namespace RepoQL.Contracts.Configuration;

/// <summary>
/// Marks a property on a <see cref="RepoQlConfig"/> nested class as a configurable setting.
/// The setting key, env var name, and registry entry are derived automatically from the property path.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string description) : Attribute
{
    /// <summary>Human-readable description shown in <c>::config</c> output.</summary>
    public string Description { get; } = description;

    /// <summary>Mask value in output and block writes to repo scope.</summary>
    public bool Sensitive { get; init; }

    /// <summary>Setting requires host restart to take effect.</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>Display hint for valid values (e.g. "none|structure|full").</summary>
    public string? ValidValues { get; init; }

    /// <summary>Display string for the default value shown in <c>::config[key]</c> output.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Old env var name for one-release compatibility bridge.
    /// When the new derived env var is absent but this legacy name is set,
    /// the loader uses it and logs a deprecation warning.
    /// Remove after one release.
    /// </summary>
    public string? LegacyEnvVar { get; init; }
}
