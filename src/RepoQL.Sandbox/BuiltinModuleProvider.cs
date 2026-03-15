using System.Collections.Concurrent;
using System.Reflection;

namespace RepoQL.Sandbox;

/// <summary>
/// Purpose: Serve built-in JavaScript library bundles from embedded resources.
/// Complexity: Static lookup from specifier to embedded resource name. Lazy-loads and caches source text.
/// </summary>
public static class BuiltinModuleProvider
{
    private static readonly string[] ModuleNames =
    [
        "yaml",
        "toml",
        "json5",
        "xml",
        "ini",
        "semver",
        "diff",
        "microdiff",
        "ohash",
        "fuse",
        "ignore",
        "base64",
        "dayjs",
        "change-case",
        "mustache",
        "radash",
        "picomatch",
        "toposort",
        "front-matter",
        "parse-diff"
    ];

    private static readonly IReadOnlyDictionary<string, string> ResourceNames = ModuleNames
        .ToDictionary(static name => name, static name => $"module.{name}.mjs", StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Assembly Assembly = typeof(BuiltinModuleProvider).Assembly;

    public static IReadOnlyList<string> AvailableModules { get; } = ModuleNames;

    public static string? Load(string specifier)
    {
        if (!TryNormalizeSpecifier(specifier, out var moduleName))
            return null;

        return Cache.GetOrAdd(moduleName, LoadEmbeddedSource);
    }

    private static bool TryNormalizeSpecifier(string specifier, out string moduleName)
    {
        moduleName = string.Empty;

        if (string.IsNullOrWhiteSpace(specifier))
            return false;

        var trimmed = specifier.Trim();
        if (trimmed.StartsWith("repoql:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["repoql:".Length..];

        if (!ResourceNames.ContainsKey(trimmed))
            return false;

        moduleName = trimmed;
        return true;
    }

    private static string LoadEmbeddedSource(string moduleName)
    {
        var resourceName = ResourceNames[moduleName];
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Built-in sandbox module '{moduleName}' was declared but embedded resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
