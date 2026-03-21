namespace RepoQL.ConsoleApp.Commands;

/// <summary>
/// Purpose: Normalize agent-friendly CLI inputs into RepoQL URI and modifier syntax.
/// Complexity: Owns path normalization, import shorthand, and read-surface validation.
/// </summary>
internal static class CliSyntax
{
    internal static string BuildReadExpression(
        string target,
        string? symbol = null,
        string? line = null,
        string? chars = null,
        string? tree = null,
        bool structure = false,
        bool headline = false,
        bool history = false,
        string? historyQuery = null,
        bool blame = false,
        bool changes = false,
        bool lint = false,
        string? lintLevel = null,
        string? find = null,
        string? similar = null,
        string? grep = null,
        string? regex = null,
        string? question = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Read target cannot be empty.");

        var trimmedTarget = target.Trim();
        if (trimmedTarget.Contains("=>", StringComparison.Ordinal))
            throw new ArgumentException("Use read flags like --tree, --find, or --question instead of `=>` syntax.");

        var modifier = BuildReadModifier(
            tree,
            structure,
            headline,
            history,
            historyQuery,
            blame,
            changes,
            lint,
            lintLevel,
            find,
            similar,
            grep,
            regex,
            question);

        var normalizedTarget = NormalizeCliUriExpression(trimmedTarget)
            ?? throw new ArgumentException("Read target cannot be empty.");
        var fragment = BuildReadFragment(normalizedTarget, symbol, line, chars);
        return modifier is null ? normalizedTarget + fragment : $"{normalizedTarget}{fragment} => {modifier}";
    }

    internal static string? NormalizeCliUriExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var trimmed = value.Trim();
        var modifierIndex = trimmed.IndexOf("=>", StringComparison.Ordinal);
        if (modifierIndex < 0)
            return NormalizeCliUriList(trimmed);

        var basePart = trimmed[..modifierIndex].Trim();
        var modifierPart = trimmed[modifierIndex..].TrimStart();
        return string.IsNullOrWhiteSpace(basePart)
            ? modifierPart
            : $"{NormalizeCliUriList(basePart)} {modifierPart}";
    }

    internal static string NormalizeCliImportUri(string value)
    {
        var trimmed = value.Trim();
        var isRemoval = trimmed.StartsWith('-');
        var body = isRemoval ? trimmed[1..].TrimStart() : trimmed;
        if (LooksLikeRepoQlUri(body))
            return isRemoval ? "-" + body : body;

        var absolutePath = Path.GetFullPath(body);
        var uri = "local:///" + absolutePath.Replace('\\', '/');
        return isRemoval ? "-" + uri : uri;
    }

    private static string BuildReadFragment(string normalizedTarget, string? symbol, string? line, string? chars)
    {
        var selectors = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["symbol"] = NullIfWhiteSpace(symbol),
            ["line"] = NullIfWhiteSpace(line),
            ["char"] = NullIfWhiteSpace(chars)
        };

        var selected = selectors.Where(pair => pair.Value is not null).ToArray();
        if (selected.Length > 1)
            throw new ArgumentException("Use only one of --symbol, --line, or --chars.");

        if (selected.Length == 0)
            return string.Empty;

        if (normalizedTarget.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException("Do not combine --symbol, --line, or --chars with a target that already contains a fragment.");

        if (normalizedTarget.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("Fragments only apply to a single target. Pass one path or URI when using --symbol, --line, or --chars.");

        var selector = selected[0];
        return $"#{selector.Key}={selector.Value}";
    }

    private static string? BuildReadModifier(
        string? tree,
        bool structure,
        bool headline,
        bool history,
        string? historyQuery,
        bool blame,
        bool changes,
        bool lint,
        string? lintLevel,
        string? find,
        string? similar,
        string? grep,
        string? regex,
        string? question)
    {
        var modifiers = new List<string>();

        if (!string.IsNullOrWhiteSpace(tree))
        {
            var normalizedTree = tree.Trim().ToLowerInvariant();
            modifiers.Add(normalizedTree switch
            {
                "folders" => "tree: folders",
                "files" => "tree: files",
                "headlines" => "tree: headlines",
                _ => throw new ArgumentException("Unknown --tree value. Use folders, files, or headlines.")
            });
        }

        if (structure)
            modifiers.Add("structure");

        if (headline)
            modifiers.Add("headline");

        if (history || !string.IsNullOrWhiteSpace(historyQuery))
        {
            var query = NullIfWhiteSpace(historyQuery);
            modifiers.Add(query is null ? "history" : $"history: {query}");
        }

        if (blame)
            modifiers.Add("blame");

        if (changes)
            modifiers.Add("changes");

        if (lint || !string.IsNullOrWhiteSpace(lintLevel))
        {
            var level = NullIfWhiteSpace(lintLevel);
            modifiers.Add(level is null ? "lint" : $"lint: {level}");
        }

        AddParameterizedModifier(modifiers, "find", find);
        AddParameterizedModifier(modifiers, "similar", similar);
        AddParameterizedModifier(modifiers, "grep", grep);
        AddParameterizedModifier(modifiers, "regex", regex);
        AddParameterizedModifier(modifiers, "question", question);

        if (modifiers.Count > 1)
            throw new ArgumentException("Use only one read view at a time, such as --tree, --structure, --find, or --question.");

        return modifiers.Count == 0 ? null : modifiers[0];
    }

    private static void AddParameterizedModifier(List<string> modifiers, string name, string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        if (normalized is not null)
            modifiers.Add($"{name}: {normalized}");
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCliUriList(string value)
        => string.Join(';', value.Split(';', StringSplitOptions.TrimEntries).Select(NormalizeCliUriPattern));

    private static string NormalizeCliUriPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return pattern;

        var trimmed = pattern.Trim();
        var isExclude = trimmed.StartsWith('!');
        if (isExclude)
            trimmed = trimmed[1..].TrimStart();

        var fragmentIndex = trimmed.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? trimmed[fragmentIndex..] : "";
        var pathPart = fragmentIndex >= 0 ? trimmed[..fragmentIndex] : trimmed;

        if (LooksLikeRepoQlUri(pathPart))
            return isExclude ? "!" + trimmed : trimmed;

        var normalizedPath = NormalizeFilePatternToUri(pathPart);
        return isExclude ? "!" + normalizedPath + fragment : normalizedPath + fragment;
    }

    private static bool LooksLikeRepoQlUri(string value)
        => value.Contains("://", StringComparison.Ordinal) ||
           value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("help:", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFilePatternToUri(string value)
    {
        var absolutePath = ResolveAbsolutePattern(value);
        return "file:///" + absolutePath.Replace('\\', '/');
    }

    private static string ResolveAbsolutePattern(string value)
    {
        var firstGlobIndex = value.IndexOfAny(['*', '?', '[']);
        if (firstGlobIndex < 0)
            return Path.GetFullPath(value);

        var prefix = value[..firstGlobIndex];
        var lastSeparator = prefix.LastIndexOfAny(['/', '\\']);
        var stableBase = lastSeparator >= 0 ? value[..(lastSeparator + 1)] : "";
        var globSuffix = value[stableBase.Length..];
        var absoluteBase = string.IsNullOrEmpty(stableBase)
            ? Path.GetFullPath(".")
            : Path.GetFullPath(stableBase);

        var separator = absoluteBase.EndsWith(Path.DirectorySeparatorChar) || absoluteBase.EndsWith(Path.AltDirectorySeparatorChar)
            ? ""
            : "/";

        return absoluteBase.Replace('\\', '/') + separator + globSuffix.Replace('\\', '/');
    }
}
