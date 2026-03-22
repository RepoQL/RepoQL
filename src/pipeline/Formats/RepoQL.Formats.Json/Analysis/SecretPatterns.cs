using System.Text.RegularExpressions;

namespace RepoQL.Formats.Json.Analysis;

/// <summary>
/// Secret detection patterns used by JSON security analysis.
///
/// Purpose: Centralizes key and value heuristics for potential secret detection.
///
/// Complexity: Fixed pattern lists and simple predicate helpers.
/// </summary>
internal static class SecretPatterns
{
    private static readonly Regex Base64SecretRegex = new(
        "^[A-Za-z0-9+/=]{20,}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<string> KeyNamePatterns { get; } =
    [
        "secret",
        "password",
        "passwd",
        "token",
        "apikey",
        "api_key",
        "api-key",
        "connectionstring",
        "connection_string",
        "connection-string"
    ];

    public static IReadOnlyList<string> ValuePrefixes { get; } =
    [
        "sk-",
        "pk-",
        "ghp_",
        "gho_",
        "github_pat_",
        "Bearer ",
        "Basic ",
        "xox"
    ];

    public static bool TryMatchKeyName(string keyName, out string matchedPattern)
    {
        matchedPattern = string.Empty;
        if (string.IsNullOrWhiteSpace(keyName))
            return false;

        foreach (var pattern in KeyNamePatterns)
        {
            if (!keyName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                continue;

            matchedPattern = pattern;
            return true;
        }

        return false;
    }

    public static bool TryMatchValuePrefix(string value, out string matchedPrefix)
    {
        matchedPrefix = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var prefix in ValuePrefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            matchedPrefix = prefix;
            return true;
        }

        return false;
    }

    public static bool LooksLikeBase64Secret(string value)
        => !string.IsNullOrWhiteSpace(value) && Base64SecretRegex.IsMatch(value);
}
