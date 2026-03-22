using System.Reflection;
using RepoQL.Contracts.Configuration;

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
    IReadOnlyList<PropertyInfo> ParentPath,
    PropertyInfo SettingProperty);

internal static class SettingDefinitionExtensions
{
    public static object? GetValue(this SettingDefinition def, RepoQlConfig config)
    {
        var container = def.GetContainer(config, createIfMissing: false);
        return container is null ? null : def.SettingProperty.GetValue(container);
    }

    public static bool TrySetValue(this SettingDefinition def, RepoQlConfig config, object? value)
    {
        var container = def.GetContainer(config, createIfMissing: true);
        if (container is null)
            return false;

        def.SettingProperty.SetValue(container, value);
        return true;
    }

    private static object? GetContainer(this SettingDefinition def, RepoQlConfig config, bool createIfMissing)
    {
        object? current = def.SectionProperty.GetValue(config);
        if (current is null)
        {
            if (!createIfMissing)
                return null;

            current = Activator.CreateInstance(def.SectionProperty.PropertyType);
            if (current is null)
                return null;
            def.SectionProperty.SetValue(config, current);
        }

        foreach (var segment in def.ParentPath)
        {
            var next = segment.GetValue(current);
            if (next is null)
            {
                if (!createIfMissing)
                    return null;

                next = Activator.CreateInstance(segment.PropertyType);
                if (next is null)
                    return null;
                segment.SetValue(current, next);
            }

            current = next;
        }

        return current;
    }
}
