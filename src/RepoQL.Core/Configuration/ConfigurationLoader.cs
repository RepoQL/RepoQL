using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;

namespace RepoQL.Core.Configuration;

/// <summary>
/// Purpose: Discovers, loads, merges, and validates configuration from all sources.
/// Complexity: Reads JSON files at three scopes + env vars, merges with precedence,
/// validates values against <see cref="SettingDefinition"/> types, tracks provenance.
/// </summary>
public static class ConfigurationLoader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads configuration from all sources, merges with precedence, and returns a <see cref="ResolvedConfig"/>.
    /// </summary>
    /// <param name="registry">The setting registry.</param>
    /// <param name="repoRoot">Repository root path, or null for non-repo contexts.</param>
    /// <param name="logger">Optional logger for warnings.</param>
    /// <param name="userConfigDir">Override for user config directory. Defaults to <c>~/.repoql</c>.</param>
    public static ResolvedConfig Load(
        SettingRegistry registry,
        string? repoRoot,
        ILogger? logger = null,
        string? userConfigDir = null)
    {
        // Layer 1: defaults (all null — consumers decide)
        var resolved = new Dictionary<string, ResolvedSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in registry.All)
            resolved[def.Key] = new ResolvedSetting(def.Key, null, ConfigScope.Default);

        // Layer 2: user scope (~/.repoql/config.json)
        var userDir = userConfigDir
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".repoql");
        var userFile = Path.Combine(userDir, "config.json");
        ApplyJsonFile(resolved, registry, userFile, ConfigScope.User, logger);

        // Layer 3: repo scope (<repo>/.repoql.json)
        if (repoRoot is not null)
        {
            var repoFile = Path.Combine(repoRoot, ".repoql.json");
            ApplyJsonFile(resolved, registry, repoFile, ConfigScope.Repo, logger);
        }

        // Layer 4: local scope (<repo>/.repoql/config.json)
        if (repoRoot is not null)
        {
            var localFile = Path.Combine(repoRoot, ".repoql", "config.json");
            ApplyJsonFile(resolved, registry, localFile, ConfigScope.Local, logger);
        }

        // Layer 5: env vars (new names)
        foreach (var def in registry.All)
        {
            var envValue = Environment.GetEnvironmentVariable(def.EnvVar);
            if (!string.IsNullOrEmpty(envValue))
            {
                var parsed = TryParse(envValue, def, logger);
                if (parsed.Success)
                    resolved[def.Key] = new ResolvedSetting(def.Key, parsed.Value, ConfigScope.Environment);
                continue;
            }

            // Layer 6: legacy env var bridge
            if (def.LegacyEnvVar is not null)
            {
                var legacyValue = Environment.GetEnvironmentVariable(def.LegacyEnvVar);
                if (!string.IsNullOrEmpty(legacyValue))
                {
                    logger?.LogWarning(
                        "Environment variable '{LegacyEnvVar}' is deprecated. Use '{NewEnvVar}' instead.",
                        def.LegacyEnvVar, def.EnvVar);

                    var parsed = TryParse(legacyValue, def, logger);
                    if (parsed.Success)
                        resolved[def.Key] = new ResolvedSetting(def.Key, parsed.Value, ConfigScope.Environment);
                }
            }
        }

        // Build the typed config object
        var config = BuildConfig(resolved, registry);
        return new ResolvedConfig(config, resolved, registry, userDir);
    }

    private static void ApplyJsonFile(
        Dictionary<string, ResolvedSetting> resolved,
        SettingRegistry registry,
        string path,
        ConfigScope scope,
        ILogger? logger)
    {
        if (!File.Exists(path))
            return;

        JsonNode? root;
        try
        {
            var json = File.ReadAllText(path);
            root = JsonNode.Parse(json, documentOptions: JsonOptions);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning("Invalid JSON in {Path}: {Message}. Skipping file.", path, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger?.LogWarning("Cannot read {Path}: {Message}. Skipping file.", path, ex.Message);
            return;
        }

        if (root is not JsonObject obj)
        {
            logger?.LogWarning("Config file {Path} is not a JSON object. Skipping file.", path);
            return;
        }

        var flat = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        Flatten(obj, "", flat);

        foreach (var (key, node) in flat)
        {
            var def = registry.TryGet(key);
            if (def is null)
            {
                logger?.LogWarning("Unknown configuration key '{Key}' in {Path}. Ignoring.", key, path);
                continue;
            }

            var parsed = TryParseJsonNode(node, def, logger, path);
            if (parsed.Success)
                resolved[key] = new ResolvedSetting(key, parsed.Value, scope);
        }
    }

    private static void Flatten(JsonObject obj, string prefix, Dictionary<string, JsonNode> result)
    {
        foreach (var (name, node) in obj)
        {
            if (node is null) continue;
            var key = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
            if (node is JsonObject nested)
                Flatten(nested, key, result);
            else
                result[key] = node;
        }
    }

    private static (bool Success, object? Value) TryParseJsonNode(
        JsonNode node, SettingDefinition def, ILogger? logger, string path)
    {
        try
        {
            var type = def.PropertyType;
            if (type == typeof(string))
                return (true, node.GetValue<string>());
            if (type == typeof(int))
                return (true, node.GetValue<int>());
            if (type == typeof(long))
                return (true, node.GetValue<long>());
            if (type == typeof(bool))
                return (true, node.GetValue<bool>());

            // Fallback: try string conversion
            var str = node.ToJsonString().Trim('"');
            return TryParse(str, def, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Invalid value for '{Key}' in {Path}: {Message}. Using default.",
                def.Key, path, ex.Message);
            return (false, null);
        }
    }

    private static (bool Success, object? Value) TryParse(string raw, SettingDefinition def, ILogger? logger)
    {
        var type = def.PropertyType;

        if (type == typeof(string))
            return (true, raw);

        if (type == typeof(int))
        {
            if (int.TryParse(raw, out var intVal))
                return (true, intVal);
            logger?.LogWarning(
                "Cannot parse '{Value}' as integer for '{Key}'. Using default.", raw, def.Key);
            return (false, null);
        }

        if (type == typeof(long))
        {
            if (long.TryParse(raw, out var longVal))
                return (true, longVal);
            logger?.LogWarning(
                "Cannot parse '{Value}' as long for '{Key}'. Using default.", raw, def.Key);
            return (false, null);
        }

        if (type == typeof(bool))
        {
            if (bool.TryParse(raw, out var boolVal))
                return (true, boolVal);
            // Also accept 0/1
            if (raw == "1") return (true, true);
            if (raw == "0") return (true, false);
            logger?.LogWarning(
                "Cannot parse '{Value}' as boolean for '{Key}'. Using default.", raw, def.Key);
            return (false, null);
        }

        // Fallback: try TypeConverter
        var converter = TypeDescriptor.GetConverter(type);
        if (converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                return (true, converter.ConvertFromInvariantString(raw));
            }
            catch
            {
                logger?.LogWarning(
                    "Cannot parse '{Value}' for '{Key}'. Using default.", raw, def.Key);
            }
        }

        return (false, null);
    }

    private static RepoQlConfig BuildConfig(
        Dictionary<string, ResolvedSetting> resolved,
        SettingRegistry registry)
    {
        var config = new RepoQlConfig();

        foreach (var def in registry.All)
        {
            if (!resolved.TryGetValue(def.Key, out var setting) || setting.Value is null)
                continue;

            var targetType = Nullable.GetUnderlyingType(def.SettingProperty.PropertyType)
                             ?? def.SettingProperty.PropertyType;

            var value = setting.Value;
            if (value.GetType() != targetType)
            {
                try { value = Convert.ChangeType(value, targetType); }
                catch { continue; }
            }

            def.TrySetValue(config, value);
        }

        return config;
    }
}
