using System.Reflection;

namespace RepoQL.Core.Configuration;

/// <summary>
/// Metadata for a single configurable setting, derived from a <see cref="Contracts.Configuration.SettingAttribute"/>
/// on a <see cref="Contracts.Configuration.RepoQlConfig"/> property.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    string EnvVar,
    string? LegacyEnvVar,
    string Description,
    Type PropertyType,
    string? DefaultValue,
    bool Sensitive,
    bool RequiresRestart,
    string? ValidValues,
    PropertyInfo SectionProperty,
    PropertyInfo SettingProperty);
