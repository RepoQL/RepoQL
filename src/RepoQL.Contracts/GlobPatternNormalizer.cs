namespace RepoQL.Contracts;

/// <summary>
/// Normalizes glob patterns by converting absolute filesystem paths to repo-relative URIs.
///
/// Purpose: Allows agents to use absolute paths in glob patterns while RepoQL works with
/// repo-relative URIs internally. This prevents common mistakes where agents pass absolute
/// paths like <c>file:///C:/repo/src/**/*.cs</c> instead of <c>file:///src/**/*.cs</c>.
///
/// Complexity: Detects and converts absolute Windows (C:\, D:\) and Unix (/home/user) paths.
/// Handles semicolon-delimited patterns with positive and negative parts, preserves fragments.
/// Uses platform-appropriate case sensitivity for path comparison.
/// </summary>
public static class GlobPatternNormalizer
{
    /// <summary>
    /// Normalizes a pattern specification by converting any absolute paths to repo-relative.
    /// </summary>
    /// <param name="patternSpec">Pattern spec (may be semicolon-delimited, may have ! prefixes).</param>
    /// <param name="repoRoot">The absolute path to the repository root.</param>
    /// <returns>Normalized pattern spec with absolute paths converted to repo-relative URIs.</returns>
    public static string? NormalizePattern(string? patternSpec, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(patternSpec))
            return patternSpec;

        // Normalize repo root for comparison: forward slashes, trailing slash
        var normalizedRoot = NormalizePathForComparison(Path.GetFullPath(repoRoot));
        if (!normalizedRoot.EndsWith('/'))
            normalizedRoot += '/';

        // Parse into individual patterns (handles semicolon delimiter)
        var parts = patternSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var isNegative = part.StartsWith('!');
            var pattern = isNegative ? part[1..].TrimStart() : part;

            var convertedPattern = NormalizeSinglePattern(pattern, normalizedRoot);

            normalized.Add(isNegative ? $"!{convertedPattern}" : convertedPattern);
        }

        return string.Join(";", normalized);
    }

    /// <summary>
    /// Normalizes a single pattern (no semicolons) by converting absolute path to relative.
    /// </summary>
    /// <param name="pattern">Single pattern (no semicolons, no ! prefix).</param>
    /// <param name="normalizedRepoRoot">Repo root with forward slashes and trailing slash.</param>
    /// <returns>Pattern with absolute path converted to repo-relative URI, or unchanged if not applicable.</returns>
    public static string NormalizeSinglePattern(string pattern, string normalizedRepoRoot)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return pattern;

        var normalized = pattern.Replace('\\', '/');

        // Extract fragment if present (e.g., #symbol=Foo, #line=10,20)
        var fragmentIndex = normalized.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? normalized[fragmentIndex..] : "";
        var pathPart = fragmentIndex >= 0 ? normalized[..fragmentIndex] : normalized;

        string? convertedPath = TryConvertToRelative(pathPart, normalizedRepoRoot);

        return convertedPath != null ? convertedPath + fragment : pattern.Replace('\\', '/');
    }

    /// <summary>
    /// Attempts to convert a path to a repo-relative file:/// URI.
    /// </summary>
    /// <param name="pathPart">The path part (no fragment).</param>
    /// <param name="normalizedRepoRoot">Repo root with forward slashes and trailing slash.</param>
    /// <returns>Repo-relative URI if the path is under the repo, null otherwise.</returns>
    private static string? TryConvertToRelative(string pathPart, string normalizedRepoRoot)
    {
        // Case 1: file:///C:/repo/path/... (Windows URI with drive letter)
        if (pathPart.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) &&
            pathPart.Length > 10 &&
            char.IsLetter(pathPart[8]) &&
            pathPart[9] == ':')
        {
            var uriPath = pathPart[8..]; // After "file:///"
            return TryMakeRelative(uriPath, normalizedRepoRoot);
        }

        // Case 2: C:/repo/path/... or C:\repo\path\... (bare Windows path)
        if (pathPart.Length >= 2 && char.IsLetter(pathPart[0]) && pathPart[1] == ':')
        {
            return TryMakeRelative(pathPart, normalizedRepoRoot);
        }

        // Case 3: /home/user/repo/... (bare Unix absolute path)
        // Only if it's truly absolute (starts with /) and NOT already a relative URI pattern
        if (pathPart.StartsWith('/') && !pathPart.StartsWith("//"))
        {
            // Don't convert if it looks like it's already relative to file:///
            // (e.g., patterns like /src/** that are meant to match file:///repo/src/**)
            // Only convert if it matches the repo root structure
            return TryMakeRelative(pathPart, normalizedRepoRoot);
        }

        return null;
    }

    /// <summary>
    /// Tries to make a path relative to the repo root.
    /// </summary>
    private static string? TryMakeRelative(string absolutePath, string normalizedRepoRoot)
    {
        try
        {
            var normalizedPath = NormalizePathForComparison(absolutePath);

            // Check if path starts with repo root (using platform-appropriate comparison)
            var comparison = GetPathComparison();
            if (normalizedPath.StartsWith(normalizedRepoRoot, comparison))
            {
                var relativePath = normalizedPath[normalizedRepoRoot.Length..];
                // Preserve trailing slash if original had one
                if (absolutePath.EndsWith('/') && !relativePath.EndsWith('/'))
                    relativePath += '/';
                return $"file:///{relativePath}";
            }

            // Also try with GetFullPath to resolve . and .. and case
            var fullPath = NormalizePathForComparison(Path.GetFullPath(absolutePath));
            if (fullPath.StartsWith(normalizedRepoRoot, comparison))
            {
                var relativePath = fullPath[normalizedRepoRoot.Length..];
                if (absolutePath.EndsWith('/') && !relativePath.EndsWith('/'))
                    relativePath += '/';
                return $"file:///{relativePath}";
            }
        }
        catch
        {
            // Invalid path - return null to leave pattern unchanged
        }

        return null;
    }

    /// <summary>
    /// Normalizes a path for comparison: forward slashes, no trailing slash (unless root).
    /// </summary>
    private static string NormalizePathForComparison(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        // Preserve trailing slash for root paths like C:/ or /
        if (normalized.Length == 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            normalized += '/';
        else if (normalized.Length == 0)
            normalized = "/";
        return normalized;
    }

    /// <summary>
    /// Gets the appropriate StringComparison for path comparison on the current platform.
    /// Windows: case-insensitive, Unix: case-sensitive.
    /// </summary>
    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>
    /// Detects if a pattern contains an absolute filesystem path.
    /// </summary>
    /// <param name="pattern">The pattern to check.</param>
    /// <returns>True if the pattern contains an absolute path.</returns>
    public static bool ContainsAbsolutePath(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var normalized = pattern.Replace('\\', '/');

        // Windows drive letter in file:// URI
        if (normalized.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) &&
            normalized.Length > 10 &&
            char.IsLetter(normalized[8]) &&
            normalized[9] == ':')
            return true;

        // Bare Windows path
        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            return true;

        // Unix absolute path (but not patterns meant to be relative like /src/**)
        // Heuristic: if it contains typical Unix home/var/usr paths, it's likely absolute
        if (normalized.StartsWith("/home/", StringComparison.Ordinal) ||
            normalized.StartsWith("/Users/", StringComparison.Ordinal) ||
            normalized.StartsWith("/var/", StringComparison.Ordinal) ||
            normalized.StartsWith("/usr/", StringComparison.Ordinal) ||
            normalized.StartsWith("/tmp/", StringComparison.Ordinal) ||
            normalized.StartsWith("/opt/", StringComparison.Ordinal))
            return true;

        return false;
    }
}
