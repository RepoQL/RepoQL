using System.Text.RegularExpressions;

namespace RepoQL.Contracts;

/// <summary>
/// Extension methods for pattern matching and scope readiness on UriRegistry.
///
/// Purpose: Provide glob matching with full wildcard support and scope readiness
/// checks for semantic search operations.
///
/// Complexity: Pattern matching iterates registry entries and applies glob/wildcard
/// logic. Supports compound patterns, negations, and fragment wildcards.
/// </summary>
public static class UriRegistryExtensions
{
    /// <summary>
    /// Matches URIs in the registry against a pattern specification.
    /// Supports compound patterns (semicolon-delimited), negations (!prefix),
    /// and full wildcards in symbol fragments (Get*, *Handler).
    /// </summary>
    /// <param name="registry">The URI registry.</param>
    /// <param name="pattern">Pattern specification.</param>
    /// <param name="ignoreCase">Whether to ignore case (default true).</param>
    /// <returns>Matching URIs (both files and symbols).</returns>
    public static IEnumerable<RepoUri> MatchPattern(
        this UriRegistry registry,
        string? pattern,
        bool ignoreCase = true)
    {
        // Blank pattern matches all files (not symbols)
        if (string.IsNullOrWhiteSpace(pattern))
        {
            foreach (var fileUri in registry.Keys)
                yield return fileUri;
            yield break;
        }

        var (positives, negatives) = UriPatternMatcher.ParsePatterns(pattern);
        var hasFragmentPattern = pattern.Contains('#', StringComparison.Ordinal);

        foreach (var (fileUri, entry) in registry)
        {
            // If pattern has no fragment, check if file matches
            if (!hasFragmentPattern)
            {
                if (MatchesPatternSet(fileUri.AbsoluteUri, positives, negatives, ignoreCase))
                {
                    yield return fileUri;
                }
            }
            else
            {
                // Pattern has fragment, check symbols
                foreach (var (symbolUri, _) in entry.Symbols)
                {
                    if (MatchesPatternSet(symbolUri.AbsoluteUri, positives, negatives, ignoreCase))
                    {
                        yield return symbolUri;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Matches file URIs only (excludes symbols).
    /// </summary>
    public static IEnumerable<RepoUri> MatchFiles(
        this UriRegistry registry,
        string? pattern,
        bool ignoreCase = true)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            foreach (var fileUri in registry.Keys)
                yield return fileUri;
            yield break;
        }

        // Strip fragment from pattern for file matching
        var containerPattern = pattern.Contains('#', StringComparison.Ordinal)
            ? pattern[..pattern.IndexOf('#', StringComparison.Ordinal)]
            : pattern;

        var (positives, negatives) = UriPatternMatcher.ParsePatterns(containerPattern);

        foreach (var fileUri in registry.Keys)
        {
            if (MatchesPatternSet(fileUri.AbsoluteUri, positives, negatives, ignoreCase))
            {
                yield return fileUri;
            }
        }
    }

    /// <summary>
    /// Checks the readiness of a scope for semantic search.
    /// </summary>
    /// <param name="registry">The URI registry.</param>
    /// <param name="pattern">Pattern specification for the scope.</param>
    /// <param name="ignoreCase">Whether to ignore case (default true).</param>
    /// <returns>Scope readiness information.</returns>
    public static ScopeReadiness CheckScope(
        this UriRegistry registry,
        string? pattern,
        bool ignoreCase = true)
    {
        var matchingFiles = registry.MatchFiles(pattern, ignoreCase).ToList();

        if (matchingFiles.Count == 0)
            return ScopeReadiness.Empty;

        var pendingIndex = new List<RepoUri>();
        var pendingEmbedding = new List<RepoUri>();
        var failed = new List<RepoUri>();
        var indexedCount = 0;
        var embeddedCount = 0;

        foreach (var fileUri in matchingFiles)
        {
            if (!registry.TryGetValue(fileUri, out var entry))
                continue;

            // Check index status
            if (entry.Status == UriStatus.Indexed)
            {
                indexedCount++;
            }
            else if (entry.Status == UriStatus.Failed)
            {
                failed.Add(fileUri);
            }
            else
            {
                pendingIndex.Add(fileUri);
            }

            // Check embedding status (only for indexed files)
            if (entry.Status == UriStatus.Indexed)
            {
                if (entry.EmbeddingStatus == EmbeddingStatus.Embedded ||
                    entry.EmbeddingStatus == EmbeddingStatus.NotApplicable)
                {
                    embeddedCount++;
                }
                else if (entry.EmbeddingStatus == EmbeddingStatus.Failed)
                {
                    if (!failed.Contains(fileUri))
                        failed.Add(fileUri);
                }
                else
                {
                    pendingEmbedding.Add(fileUri);
                }
            }
        }

        return new ScopeReadiness(
            TotalFiles: matchingFiles.Count,
            IndexedCount: indexedCount,
            EmbeddedCount: embeddedCount,
            PendingIndex: pendingIndex,
            PendingEmbedding: pendingEmbedding,
            FailedFiles: failed);
    }

    /// <summary>
    /// Gets a summary of the registry state.
    /// </summary>
    public static RegistrySummary GetSummary(this UriRegistry registry)
    {
        var totalFiles = 0;
        var totalSymbols = 0;
        var byStatus = new Dictionary<UriStatus, int>();
        var byEmbedding = new Dictionary<EmbeddingStatus, int>();

        foreach (var status in Enum.GetValues<UriStatus>())
            byStatus[status] = 0;
        foreach (var status in Enum.GetValues<EmbeddingStatus>())
            byEmbedding[status] = 0;

        foreach (var (_, entry) in registry)
        {
            totalFiles++;
            totalSymbols += entry.Symbols.Count;
            byStatus[entry.Status]++;
            byEmbedding[entry.EmbeddingStatus]++;
        }

        return new RegistrySummary(
            TotalFiles: totalFiles,
            TotalSymbols: totalSymbols,
            ByStatus: byStatus,
            ByEmbeddingStatus: byEmbedding);
    }

    private static bool MatchesPatternSet(
        string uri,
        string[] positives,
        string[] negatives,
        bool ignoreCase)
    {
        // If only negatives, match everything except those
        if (positives.Length == 0)
        {
            foreach (var neg in negatives)
            {
                if (MatchesSinglePattern(uri, neg, ignoreCase))
                    return false;
            }
            return true;
        }

        // Must match at least one positive
        var matchedPositive = false;
        foreach (var pos in positives)
        {
            if (MatchesSinglePattern(uri, pos, ignoreCase))
            {
                matchedPositive = true;
                break;
            }
        }

        if (!matchedPositive)
            return false;

        // Must not match any negative
        foreach (var neg in negatives)
        {
            if (MatchesSinglePattern(uri, neg, ignoreCase))
                return false;
        }

        return true;
    }

    private static bool MatchesSinglePattern(string uri, string pattern, bool ignoreCase)
    {
        // Split into container and fragment
        var uriHashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        var patternHashIndex = pattern.IndexOf('#', StringComparison.Ordinal);

        var uriContainer = uriHashIndex >= 0 ? uri[..uriHashIndex] : uri;
        var uriFragment = uriHashIndex >= 0 ? uri[(uriHashIndex + 1)..] : null;

        var patternContainer = patternHashIndex >= 0 ? pattern[..patternHashIndex] : pattern;
        var patternFragment = patternHashIndex >= 0 ? pattern[(patternHashIndex + 1)..] : null;

        // Match container using existing glob logic
        if (UriPatternMatcher.Matches(uriContainer, patternContainer, ignoreCase) != true)
            return false;

        // If pattern has no fragment, container match is sufficient
        if (patternFragment is null)
            return true;

        // Pattern has fragment, URI must too
        if (uriFragment is null)
            return false;

        // Match fragment with full wildcard support
        return MatchesFragment(uriFragment, patternFragment, ignoreCase);
    }

    private static bool MatchesFragment(string uriFragment, string patternFragment, bool ignoreCase)
    {
        // Parse fragments into key=value pairs
        var uriParams = ParseFragmentParams(uriFragment);
        var patternParams = ParseFragmentParams(patternFragment);

        // Each pattern param must match corresponding URI param
        foreach (var (key, patternValue) in patternParams)
        {
            if (!uriParams.TryGetValue(key, out var uriValue))
                return false;

            if (!MatchesWithWildcard(uriValue, patternValue, ignoreCase))
                return false;
        }

        return true;
    }

    private static Dictionary<string, string> ParseFragmentParams(string fragment)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        // Handle plain anchor (no =)
        if (!fragment.Contains('=', StringComparison.Ordinal))
        {
            result["anchor"] = fragment;
            return result;
        }

        foreach (var part in fragment.Split('&'))
        {
            var eqIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex > 0)
            {
                var key = part[..eqIndex];
                var value = part[(eqIndex + 1)..];
                result[key] = Uri.UnescapeDataString(value);
            }
        }

        return result;
    }

    private static bool MatchesWithWildcard(string value, string pattern, bool ignoreCase)
    {
        // Convert glob pattern to regex
        // * = any characters, ? = single character
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", "☆", StringComparison.Ordinal) // Preserve ** temporarily
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal)
            .Replace("☆", ".*", StringComparison.Ordinal) // ** also becomes .*
            + "$";

        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;

        return Regex.IsMatch(value, regexPattern, options);
    }
}

/// <summary>
/// Summary statistics for the URI registry.
/// </summary>
public record RegistrySummary(
    int TotalFiles,
    int TotalSymbols,
    IReadOnlyDictionary<UriStatus, int> ByStatus,
    IReadOnlyDictionary<EmbeddingStatus, int> ByEmbeddingStatus);
