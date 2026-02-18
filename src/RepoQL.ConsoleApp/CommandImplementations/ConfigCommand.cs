using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RepoQL.Commands;
using RepoQL.Contracts.Configuration;
using RepoQL.Core.Configuration;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Inspect and mutate RepoQL settings across local, repo, and user scopes.
/// Complexity: Key discovery from SettingRegistry, type validation, provenance rendering,
/// Levenshtein suggestions, and atomic JSON updates with scope-specific file paths.
/// </summary>
[CommandClass]
internal sealed class ConfigCommand(SettingRegistry registry, ResolvedConfig config, string repoRoot)
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _repoRoot = Path.GetFullPath(repoRoot);

    [Command("config", Description = "List all settings with values, sources, and descriptions")]
    public Task<CommandResult> List(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Reload();

        var sections = registry.All
            .OrderBy(def => def.Key, StringComparer.OrdinalIgnoreCase)
            .GroupBy(def => SectionName(def.Key), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sections.Count == 0)
            return Task.FromResult(CommandResult.Success("No settings registered."));

        var sb = new StringBuilder();
        var firstSection = true;

        foreach (var section in sections)
        {
            if (!firstSection)
                sb.AppendLine();
            firstSection = false;

            sb.AppendLine(section.Key);

            var rows = section
                .OrderBy(def => def.Key, StringComparer.OrdinalIgnoreCase)
                .Select(def =>
                {
                    var resolved = config.GetProvenance(def.Key) ?? new ResolvedSetting(def.Key, null, ConfigScope.Default);
                    return new Row(
                        SettingName(def.Key),
                        RenderValue(resolved.Value, def.Sensitive),
                        SourceLabel(resolved.Source),
                        def.Description);
                })
                .ToList();

            var maxName = rows.Max(row => row.Name.Length);
            var maxValue = rows.Max(row => row.Value.Length);

            foreach (var row in rows)
            {
                sb.AppendLine(
                    $"  {row.Name.PadRight(maxName)}  = {row.Value.PadRight(maxValue)}  ({row.Source})   {row.Description}");
            }
        }

        return Task.FromResult(CommandResult.Success(sb.ToString().TrimEnd()));
    }

    [Command("config.read", Description = "Show details for a single setting")]
    public Task<CommandResult> Read(
        [CommandParam("Setting key (e.g. duckdb.memory_limit)")] string key,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return Task.FromResult(CommandResult.Error("Setting key is required."));

        var def = registry.TryGet(normalizedKey);
        if (def is null)
            return Task.FromResult(UnknownKey(normalizedKey));

        Reload();
        var resolved = config.GetProvenance(def.Key) ?? new ResolvedSetting(def.Key, null, ConfigScope.Default);

        var sb = new StringBuilder();
        sb.AppendLine(def.Key);
        sb.AppendLine($"  Value:          {RenderValue(resolved.Value, def.Sensitive)}");
        sb.AppendLine($"  Source:         {SourceLabel(resolved.Source)}");
        sb.AppendLine($"  Default:        {def.DefaultValue ?? "(none)"}");
        sb.AppendLine($"  Env var:        {def.EnvVar}");
        sb.AppendLine($"  Legacy env var: {def.LegacyEnvVar ?? "(none)"}");
        sb.AppendLine($"  Valid values:   {def.ValidValues ?? "(any)"}");
        sb.AppendLine($"  Restart:        {(def.RequiresRestart ? "yes" : "no")}");
        sb.Append($"  Description:    {def.Description}");

        return Task.FromResult(CommandResult.Success(sb.ToString()));
    }

    [Command("config.set", Description = "Set a configuration value")]
    public Task<CommandResult> Set(
        [CommandParam("Setting key")] string key,
        [CommandParam("Value to set")] string value,
        [CommandParam("Scope: local (default), repo, or user")] string? scope,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return Task.FromResult(CommandResult.Error("Setting key is required."));

        var def = registry.TryGet(normalizedKey);
        if (def is null)
            return Task.FromResult(UnknownKey(normalizedKey));

        if (!TryParseWriteScope(scope, out var writeScope, out var scopeError))
            return Task.FromResult(CommandResult.Error(scopeError!));

        if (def.Sensitive && writeScope == WriteScope.Repo)
        {
            return Task.FromResult(CommandResult.Error(
                $"Setting '{def.Key}' is sensitive and cannot be written to repo scope. " +
                $"Use local or user scope, or set {def.EnvVar} in your environment."));
        }

        if (!TryParseValue(value, def.PropertyType, out var parsedValue, out var parseError))
            return Task.FromResult(CommandResult.Error(BuildParseError(def, parseError)));

        var path = PathForScope(writeScope);
        var root = ReadConfigObject(path, out var readError);
        if (root is null)
            return Task.FromResult(CommandResult.Error(readError!));

        var valueNode = parsedValue is null
            ? null
            : JsonSerializer.SerializeToNode(parsedValue, parsedValue.GetType());
        SetKey(root, def.Key, valueNode);

        try
        {
            WriteAtomic(path, root);
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Error($"Failed to write {path}: {ex.Message}"));
        }

        Reload();

        var displayValue = RenderValue(parsedValue, def.Sensitive);
        var message = $"Set {def.Key} = {displayValue} ({ScopeLabel(writeScope)})";
        if (def.RequiresRestart)
            message += Environment.NewLine + "Restart required: yes (run ::host.restart)";

        return Task.FromResult(CommandResult.Success(message));
    }

    [Command("config.reset", Description = "Remove a setting from a scope")]
    public Task<CommandResult> Reset(
        [CommandParam("Setting key")] string key,
        [CommandParam("Scope: local (default), repo, or user")] string? scope,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeKey(key, allowResetPrefix: true);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return Task.FromResult(CommandResult.Error("Setting key is required."));

        var def = registry.TryGet(normalizedKey);
        if (def is null)
            return Task.FromResult(UnknownKey(normalizedKey));

        if (!TryParseWriteScope(scope, out var writeScope, out var scopeError))
            return Task.FromResult(CommandResult.Error(scopeError!));

        var path = PathForScope(writeScope);
        if (File.Exists(path))
        {
            var root = ReadConfigObject(path, out var readError);
            if (root is null)
                return Task.FromResult(CommandResult.Error(readError!));

            var removed = RemoveKey(root, def.Key);
            if (removed)
            {
                try
                {
                    WriteAtomic(path, root);
                }
                catch (Exception ex)
                {
                    return Task.FromResult(CommandResult.Error($"Failed to write {path}: {ex.Message}"));
                }
            }
        }

        Reload();
        return Task.FromResult(CommandResult.Success($"Reset {def.Key} ({ScopeLabel(writeScope)})"));
    }

    private void Reload() =>
        config.Reload(_repoRoot, userConfigDir: config.UserConfigDir);

    private CommandResult UnknownKey(string key)
    {
        var suggestion = FindClosestKey(key);
        var message = suggestion is null
            ? $"Unknown setting '{key}'."
            : $"Unknown setting '{key}'. Did you mean '{suggestion}'?";
        return CommandResult.Error(message);
    }

    private string? FindClosestKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var keys = registry.All.Select(def => def.Key).ToList();
        if (keys.Count == 0)
            return null;

        var best = keys
            .Select(candidate => (Candidate: candidate, Distance: CommandRegistry.LevenshteinDistance(
                key.ToLowerInvariant(),
                candidate.ToLowerInvariant())))
            .OrderBy(item => item.Distance)
            .First();

        return best.Distance <= (key.Length / 2) + 2 ? best.Candidate : null;
    }

    private string PathForScope(WriteScope scope) =>
        scope switch
        {
            WriteScope.Local => Path.Combine(_repoRoot, ".repoql", "config.json"),
            WriteScope.Repo => Path.Combine(_repoRoot, ".repoql.json"),
            WriteScope.User => Path.Combine(config.UserConfigDir, "config.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

    private static bool TryParseWriteScope(string? value, out WriteScope scope, out string? error)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "local" : value.Trim();
        if (raw.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            scope = WriteScope.Local;
            error = null;
            return true;
        }

        if (raw.Equals("repo", StringComparison.OrdinalIgnoreCase))
        {
            scope = WriteScope.Repo;
            error = null;
            return true;
        }

        if (raw.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            scope = WriteScope.User;
            error = null;
            return true;
        }

        scope = WriteScope.Local;
        error = $"Invalid scope '{raw}'. Expected local, repo, or user.";
        return false;
    }

    private static bool TryParseValue(string raw, Type type, out object? parsed, out string error)
    {
        if (type == typeof(string))
        {
            parsed = raw;
            error = string.Empty;
            return true;
        }

        if (type == typeof(int))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                parsed = intValue;
                error = string.Empty;
                return true;
            }

            parsed = null;
            error = $"expected integer, got '{raw}'";
            return false;
        }

        if (type == typeof(long))
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            {
                parsed = longValue;
                error = string.Empty;
                return true;
            }

            parsed = null;
            error = $"expected long, got '{raw}'";
            return false;
        }

        if (type == typeof(bool))
        {
            if (bool.TryParse(raw, out var boolValue))
            {
                parsed = boolValue;
                error = string.Empty;
                return true;
            }

            if (raw == "1")
            {
                parsed = true;
                error = string.Empty;
                return true;
            }

            if (raw == "0")
            {
                parsed = false;
                error = string.Empty;
                return true;
            }

            parsed = null;
            error = $"expected boolean, got '{raw}'";
            return false;
        }

        var converter = TypeDescriptor.GetConverter(type);
        if (converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                var converted = converter.ConvertFromInvariantString(raw);
                if (converted is not null)
                {
                    parsed = converted;
                    error = string.Empty;
                    return true;
                }
            }
            catch
            {
                // fall through to common error
            }
        }

        parsed = null;
        error = $"expected {type.Name}, got '{raw}'";
        return false;
    }

    private static JsonObject? ReadConfigObject(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
            return new JsonObject();

        try
        {
            var text = File.ReadAllText(path);
            var parsed = JsonNode.Parse(text, documentOptions: JsonOptions);
            if (parsed is null)
                return new JsonObject();
            if (parsed is JsonObject obj)
                return obj;

            error = $"Config file {path} must contain a top-level JSON object.";
            return null;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON in {path}: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"Failed to read {path}: {ex.Message}";
            return null;
        }
    }

    private static void WriteAtomic(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tmpPath = path + ".tmp";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmpPath, json);
        try
        {
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
    }

    private static void SetKey(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[parts[i]] = next;
            }

            current = next;
        }

        current[parts[^1]] = value;
    }

    private static bool RemoveKey(JsonObject root, string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        return RemoveCore(root, parts, 0);
    }

    private static bool RemoveCore(JsonObject current, string[] parts, int index)
    {
        var name = parts[index];
        if (index == parts.Length - 1)
            return current.Remove(name);

        if (current[name] is not JsonObject next)
            return false;

        var removed = RemoveCore(next, parts, index + 1);
        if (removed && next.Count == 0)
            current.Remove(name);

        return removed;
    }

    private static string NormalizeKey(string key) => NormalizeKey(key, allowResetPrefix: false);

    private static string NormalizeKey(string key, bool allowResetPrefix)
    {
        var normalized = key.Trim();
        if (allowResetPrefix && normalized.StartsWith("-", StringComparison.Ordinal))
            normalized = normalized[1..];
        return normalized.ToLowerInvariant();
    }

    private static string BuildParseError(SettingDefinition def, string parseError)
    {
        var sb = new StringBuilder();
        sb.Append($"Invalid value for '{def.Key}': {parseError}.");
        if (!string.IsNullOrWhiteSpace(def.ValidValues))
            sb.Append($" Valid values: {def.ValidValues}.");
        return sb.ToString();
    }

    private static string SectionName(string key)
    {
        var idx = key.IndexOf('.');
        return idx < 0 ? key : key[..idx];
    }

    private static string SettingName(string key)
    {
        var idx = key.IndexOf('.');
        return idx < 0 ? key : key[(idx + 1)..];
    }

    private static string RenderValue(object? value, bool sensitive)
    {
        if (value is null)
            return "<not set>";

        var asText = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!sensitive)
            return asText;

        if (asText.Length < 8)
            return "****";

        return $"{asText[..4]}****{asText[^2..]}";
    }

    private static string SourceLabel(ConfigScope source) => source.ToString().ToLowerInvariant();

    private static string ScopeLabel(WriteScope scope) => scope.ToString().ToLowerInvariant();

    private readonly record struct Row(string Name, string Value, string Source, string Description);

    private enum WriteScope
    {
        Local,
        Repo,
        User,
    }
}
