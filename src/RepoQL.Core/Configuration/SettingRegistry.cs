using System.Reflection;
using System.Text;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Core.Configuration;

/// <summary>
/// Purpose: Discovers and indexes all settings defined on <see cref="RepoQlConfig"/> via reflection.
/// Complexity: Reflects over nested types on RepoQlConfig, finds [Setting]-annotated properties,
/// derives keys and env var names from the property path. Built once at startup.
/// </summary>
public sealed class SettingRegistry
{
    private readonly Dictionary<string, SettingDefinition> _byKey;

    private SettingRegistry(Dictionary<string, SettingDefinition> byKey)
    {
        _byKey = byKey;
    }

    public IReadOnlyDictionary<string, SettingDefinition> Settings => _byKey;

    public IEnumerable<SettingDefinition> All => _byKey.Values;

    public SettingDefinition? TryGet(string key)
        => _byKey.TryGetValue(key, out var def) ? def : null;

    /// <summary>
    /// Builds the registry by reflecting over <see cref="RepoQlConfig"/>.
    /// </summary>
    public static SettingRegistry Build()
    {
        var entries = new Dictionary<string, SettingDefinition>(StringComparer.OrdinalIgnoreCase);

        var configType = typeof(RepoQlConfig);
        foreach (var sectionProp in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var sectionType = sectionProp.PropertyType;
            if (sectionType.DeclaringType != configType)
                continue; // only nested types

            var sectionName = sectionProp.Name.ToLowerInvariant();

            foreach (var settingProp in sectionType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = settingProp.GetCustomAttribute<SettingAttribute>();
                if (attr is null)
                    continue;

                var settingName = ToSnakeCase(settingProp.Name);
                var key = $"{sectionName}.{settingName}";
                var envVar = DeriveEnvVar(key);

                entries[key] = new SettingDefinition(
                    Key: key,
                    EnvVar: envVar,
                    LegacyEnvVar: attr.LegacyEnvVar,
                    Description: attr.Description,
                    PropertyType: Nullable.GetUnderlyingType(settingProp.PropertyType) ?? settingProp.PropertyType,
                    DefaultValue: attr.DefaultValue,
                    Sensitive: attr.Sensitive,
                    RequiresRestart: attr.RequiresRestart,
                    ValidValues: attr.ValidValues,
                    SectionProperty: sectionProp,
                    SettingProperty: settingProp);
            }
        }

        return new SettingRegistry(entries);
    }

    /// <summary>
    /// Derives the environment variable name from a setting key.
    /// <c>duckdb.memory_limit</c> → <c>REPOQL_DUCKDB_MEMORY_LIMIT</c>
    /// </summary>
    public static string DeriveEnvVar(string key)
        => "REPOQL_" + key.Replace('.', '_').ToUpperInvariant();

    /// <summary>
    /// Converts PascalCase to snake_case.
    /// <c>MemoryLimit</c> → <c>memory_limit</c>
    /// </summary>
    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    // Insert underscore before uppercase unless previous was also uppercase
                    // and next (if exists) is also uppercase (handles acronyms like "TTL")
                    var prev = name[i - 1];
                    if (!char.IsUpper(prev))
                    {
                        sb.Append('_');
                    }
                    else if (i + 1 < name.Length && char.IsLower(name[i + 1]))
                    {
                        // End of acronym, e.g. "TTLSeconds" → "ttl_seconds"
                        sb.Append('_');
                    }
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
