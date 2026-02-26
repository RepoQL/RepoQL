namespace RepoQL.Sarif.Normalization;

/// <summary>
/// Normalizes SARIF artifact paths into repo-relative forward-slash paths.
/// </summary>
public sealed class PathNormalizer
{
    private static readonly HashSet<string> KnownRepoRootBaseIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "%SRCROOT%",
        "SRCROOT",
        "ROOTPATH"
    };

    /// <summary>
    /// Normalize a SARIF artifact URI/path into a repo-relative path.
    /// Unresolvable paths are preserved and surfaced via warnings.
    /// </summary>
    public string Normalize(
        string rawUri,
        string? uriBaseId,
        IReadOnlyDictionary<string, string> originalUriBaseIds,
        string repoRootPath,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
            return string.Empty;

        var decodedUri = Uri.UnescapeDataString(rawUri);
        if (HasUnsupportedScheme(decodedUri))
        {
            warnings.Add($"Path '{rawUri}' has an unsupported URI scheme and was preserved.");
            return decodedUri;
        }

        var hasFileScheme = decodedUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        var stripped = StripFileScheme(decodedUri);
        var candidatePath = NormalizeSeparators(stripped);

        var resolvedBase = ResolveBasePath(uriBaseId, originalUriBaseIds, repoRootPath, warnings);
        if (!string.IsNullOrWhiteSpace(resolvedBase) && !IsAbsolutePath(candidatePath))
            candidatePath = CombinePath(resolvedBase!, candidatePath);

        candidatePath = NormalizeSeparators(candidatePath);
        var repoRoot = NormalizeSeparators(repoRootPath).TrimEnd('/');

        if (TryRelativize(candidatePath, repoRoot, out var relative))
            return NormalizeRelative(relative);

        // sonar-tools often emits file:///src/... which behaves like repo-relative despite file scheme.
        if (hasFileScheme && candidatePath.StartsWith("/", StringComparison.Ordinal) && !IsWindowsAbsolute(candidatePath))
            return NormalizeRelative(candidatePath);

        if (IsAbsolutePath(candidatePath))
        {
            warnings.Add($"Path '{rawUri}' is outside repository root '{repoRootPath}' and was preserved.");
            return NormalizeSeparators(candidatePath);
        }

        return NormalizeRelative(candidatePath);
    }

    private static string? ResolveBasePath(
        string? uriBaseId,
        IReadOnlyDictionary<string, string> originalUriBaseIds,
        string repoRootPath,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(uriBaseId))
            return null;

        if (originalUriBaseIds.TryGetValue(uriBaseId, out var baseUri))
            return NormalizeBaseUri(baseUri, repoRootPath, warnings);

        if (KnownRepoRootBaseIds.Contains(uriBaseId))
            return NormalizeSeparators(repoRootPath);

        return null;
    }

    private static string NormalizeBaseUri(string baseUri, string repoRootPath, ICollection<string> warnings)
    {
        var decoded = Uri.UnescapeDataString(baseUri);
        if (HasUnsupportedScheme(decoded))
        {
            warnings.Add($"uriBaseId value '{baseUri}' has unsupported scheme; repo root fallback used.");
            return NormalizeSeparators(repoRootPath);
        }

        var stripped = NormalizeSeparators(StripFileScheme(decoded));
        if (IsAbsolutePath(stripped))
            return stripped;

        return CombinePath(repoRootPath, stripped);
    }

    private static bool HasUnsupportedScheme(string value)
    {
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
            return false;

        return !value.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripFileScheme(string value)
    {
        if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            return value["file:///".Length..];

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return value["file://".Length..];

        return value;
    }

    private static bool TryRelativize(string candidatePath, string repoRoot, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(repoRoot))
            return false;

        var normalizedCandidate = NormalizeSeparators(candidatePath).TrimEnd('/');
        var normalizedRoot = NormalizeSeparators(repoRoot).TrimEnd('/');

        var comparison = IsWindowsAbsolute(normalizedRoot)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
        {
            relative = string.Empty;
            return true;
        }

        if (!normalizedCandidate.StartsWith($"{normalizedRoot}/", comparison))
            return false;

        relative = normalizedCandidate[(normalizedRoot.Length + 1)..];
        return true;
    }

    private static string NormalizeRelative(string value)
    {
        var normalized = NormalizeSeparators(value).TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private static string NormalizeSeparators(string value)
    {
        return value.Replace('\\', '/');
    }

    private static bool IsAbsolutePath(string value)
    {
        return IsWindowsAbsolute(value) || value.StartsWith("/", StringComparison.Ordinal);
    }

    private static bool IsWindowsAbsolute(string value)
    {
        return value.Length >= 3
               && char.IsLetter(value[0])
               && value[1] == ':'
               && (value[2] == '/' || value[2] == '\\');
    }

    private static string CombinePath(string left, string right)
    {
        var normalizedLeft = NormalizeSeparators(left).TrimEnd('/');
        var normalizedRight = NormalizeSeparators(right).TrimStart('/');
        return string.IsNullOrEmpty(normalizedLeft)
            ? normalizedRight
            : $"{normalizedLeft}/{normalizedRight}";
    }
}
