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
    /// Matches URIs in the registry against a pattern specification using line-range-based matching.
    /// Supports compound patterns (semicolon-delimited), negations (!prefix),
    /// and full wildcards in symbol fragments (Get*, *Handler).
    ///
    /// Pattern matching flow:
    /// 1. Parse pattern into positives and negatives
    /// 2. For each matching file, expand entities to line ranges
    /// 3. Union positive ranges, subtract negative ranges
    /// 4. Simplify results to canonical URIs (file, symbol, or line range)
    /// </summary>
    /// <param name="registry">The URI registry.</param>
    /// <param name="pattern">Pattern specification.</param>
    /// <param name="ignoreCase">Whether to ignore case (default true).</param>
    /// <returns>Matching URIs (files, symbols, or line ranges).</returns>
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
        var parsedPositives = positives.Select(ParsePatternComponents).ToArray();
        var parsedNegatives = negatives.Select(ParsePatternComponents).ToArray();

        // Snapshot the registry to avoid mid-operation updates
        var snapshot = registry.ToList();

        foreach (var (fileUri, entry) in snapshot)
        {
            // Collect positive ranges and spanless symbols for this file
            var positiveRanges = new List<LineRange>();
            var spanlessPositiveSymbols = new List<RepoUri>();

            foreach (var parsed in parsedPositives)
            {
                if (!MatchesContainer(fileUri.AbsoluteUri, parsed.Container, ignoreCase))
                    continue;

                var expansion = ExpandToRangesAndSymbols(fileUri, entry, parsed.Fragment, ignoreCase);
                positiveRanges.AddRange(expansion.Ranges);
                spanlessPositiveSymbols.AddRange(expansion.SpanlessSymbols);
            }

            // If no positive patterns specified but negatives exist, start with whole file
            if (positives.Length == 0 && negatives.Length > 0)
            {
                positiveRanges.Add(LineRange.WholeFile(entry.LineCount));
            }

            // Process line ranges if any
            if (positiveRanges.Count > 0)
            {
                // Union positive ranges
                var unionedRanges = positiveRanges.Union();

                // Collect negative ranges for this file
                var negativeRanges = new List<LineRange>();
                foreach (var parsed in parsedNegatives)
                {
                    // Check if negative applies to this file
                    if (!string.IsNullOrEmpty(parsed.Container) &&
                        !MatchesContainer(fileUri.AbsoluteUri, parsed.Container, ignoreCase))
                        continue;

                    var expansion = ExpandToRangesAndSymbols(fileUri, entry, parsed.Fragment, ignoreCase);
                    negativeRanges.AddRange(expansion.Ranges);
                }

                // Subtract negative ranges
                var finalRanges = unionedRanges.Subtract(negativeRanges.Union());

                // Simplify and yield results
                foreach (var range in finalRanges)
                {
                    yield return UriSimplifier.Simplify(fileUri, range, entry);
                }
            }

            // Process spanless symbols - check against negative symbol patterns
            foreach (var symbolUri in spanlessPositiveSymbols)
            {
                if (!IsSymbolExcludedByNegatives(symbolUri, parsedNegatives, ignoreCase))
                {
                    yield return symbolUri;
                }
            }
        }
    }

    /// <summary>
    /// Checks if a symbol URI is excluded by any negative pattern.
    /// Only symbol= patterns can exclude spanless symbols (line patterns can't apply).
    /// </summary>
    private static bool IsSymbolExcludedByNegatives(
        RepoUri symbolUri,
        ParsedPattern[] negatives,
        bool ignoreCase)
    {
        var symbolName = ExtractSymbolName(symbolUri);
        var anchorName = ExtractAnchorName(symbolUri);

        foreach (var parsed in negatives)
        {
            // Check container match (if specified)
            if (!string.IsNullOrEmpty(parsed.Container) &&
                !MatchesContainer(symbolUri.Container.AbsoluteUri, parsed.Container, ignoreCase))
                continue;

            // Only symbol= fragments can exclude spanless symbols
            if (string.IsNullOrEmpty(parsed.Fragment))
                continue; // No fragment = whole file exclusion, doesn't apply to specific symbols

            var fragmentParams = ParseFragmentParams(parsed.Fragment);
            if (fragmentParams.TryGetValue("symbol", out var negativePattern))
            {
                if (MatchesWithWildcard(symbolName, negativePattern, ignoreCase))
                    return true; // Symbol is excluded
            }

            if (fragmentParams.TryGetValue("anchor", out var anchorPattern))
            {
                if (!string.IsNullOrEmpty(anchorName) &&
                    MatchesWithWildcard(anchorName, anchorPattern, ignoreCase))
                {
                    return true; // Anchor is excluded
                }
            }
            // Note: line= fragments can't exclude spanless symbols (we don't know their location)
        }

        return false;
    }

    /// <summary>
    /// Parsed components of a pattern (container and fragment).
    /// </summary>
    private readonly record struct ParsedPattern(string Container, string? Fragment);

    /// <summary>
    /// Result of expanding a pattern to line ranges, including spanless symbol matches.
    /// </summary>
    private readonly record struct ExpansionResult(
        IReadOnlyList<LineRange> Ranges,
        IReadOnlyList<RepoUri> SpanlessSymbols)
    {
        public static ExpansionResult Empty => new([], []);
    }

    /// <summary>
    /// Parses a pattern into container and fragment components.
    /// </summary>
    private static ParsedPattern ParsePatternComponents(string pattern)
    {
        var hashIndex = pattern.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex < 0)
            return new ParsedPattern(pattern, null);

        var container = hashIndex > 0 ? pattern[..hashIndex] : "";
        var fragment = pattern[(hashIndex + 1)..];
        return new ParsedPattern(container, fragment);
    }

    /// <summary>
    /// Checks if a URI matches a container pattern.
    /// </summary>
    private static bool MatchesContainer(string uri, string containerPattern, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(containerPattern))
            return true;

        return UriPatternMatcher.Matches(uri, containerPattern, ignoreCase) == true;
    }

    /// <summary>
    /// Expands a fragment pattern to line ranges for a file.
    /// Symbols with spans become line ranges; symbols without spans are returned separately.
    /// </summary>
    private static ExpansionResult ExpandToRangesAndSymbols(
        RepoUri fileUri,
        FileEntry entry,
        string? fragment,
        bool ignoreCase)
    {
        // No fragment = whole file (uses WholeFileUnknown sentinel when LineCount == 0)
        if (string.IsNullOrEmpty(fragment))
        {
            return new ExpansionResult([LineRange.WholeFile(entry.LineCount)], []);
        }

        // Parse fragment
        var fragmentParams = ParseFragmentParams(fragment);

        // Handle line= fragment
        if (fragmentParams.TryGetValue("line", out var lineValue))
        {
            var range = ParseLineRange(lineValue);
            return range.IsValid
                ? new ExpansionResult([range], [])
                : ExpansionResult.Empty;
        }

        // Handle symbol= fragment
        if (fragmentParams.TryGetValue("symbol", out var symbolPattern))
        {
            var ranges = new List<LineRange>();
            var spanlessSymbols = new List<RepoUri>();

            foreach (var (symbolUri, symbolEntry) in entry.Symbols)
            {
                // Match symbol name against pattern
                var symbolName = ExtractSymbolName(symbolUri);
                if (MatchesWithWildcard(symbolName, symbolPattern, ignoreCase))
                {
                    if (symbolEntry.HasSpan)
                    {
                        // Symbol has span - use line range for set operations
                        ranges.Add(new LineRange(symbolEntry.StartLine, symbolEntry.EndLine));
                    }
                    else
                    {
                        // Symbol has no span - collect for direct output
                        spanlessSymbols.Add(symbolUri);
                    }
                }
            }

            return new ExpansionResult(ranges, spanlessSymbols);
        }

        // Handle plain anchor fragments (#slug)
        if (fragmentParams.TryGetValue("anchor", out var anchorPattern))
        {
            var ranges = new List<LineRange>();
            var spanlessSymbols = new List<RepoUri>();

            foreach (var (symbolUri, symbolEntry) in entry.Symbols)
            {
                var anchorName = ExtractAnchorName(symbolUri);
                if (string.IsNullOrEmpty(anchorName) || !MatchesWithWildcard(anchorName, anchorPattern, ignoreCase))
                    continue;

                if (symbolEntry.HasSpan)
                {
                    ranges.Add(new LineRange(symbolEntry.StartLine, symbolEntry.EndLine));
                }
                else
                {
                    spanlessSymbols.Add(symbolUri);
                }
            }

            return new ExpansionResult(ranges, spanlessSymbols);
        }

        // Unknown fragment type - no results
        return ExpansionResult.Empty;
    }

    /// <summary>
    /// Extracts the symbol name from a symbol URI fragment.
    /// </summary>
    private static string ExtractSymbolName(RepoUri symbolUri)
    {
        var fragment = symbolUri.Fragment;
        if (string.IsNullOrEmpty(fragment))
            return string.Empty;

        // Remove leading #
        if (fragment.StartsWith('#'))
            fragment = fragment[1..];

        // Parse symbol= value
        const string prefix = "symbol=";
        var symbolIndex = fragment.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (symbolIndex < 0)
            return fragment;

        var valueStart = symbolIndex + prefix.Length;
        var valueEnd = fragment.IndexOf('&', valueStart);
        return valueEnd < 0
            ? Uri.UnescapeDataString(fragment[valueStart..])
            : Uri.UnescapeDataString(fragment[valueStart..valueEnd]);
    }

    /// <summary>
    /// Extracts the anchor slug from a URI fragment like #heading-slug.
    /// Returns empty when the URI does not use an anchor fragment.
    /// </summary>
    private static string ExtractAnchorName(RepoUri symbolUri)
    {
        var fragment = symbolUri.Fragment;
        if (string.IsNullOrEmpty(fragment))
            return string.Empty;

        var parsed = ParseFragmentParams(fragment);
        return parsed.TryGetValue("anchor", out var anchor) ? anchor : string.Empty;
    }

    /// <summary>
    /// Parses a line range value (e.g., "10,20" or "10").
    /// </summary>
    private static LineRange ParseLineRange(string value)
    {
        var parts = value.Split(',');
        if (parts.Length == 1 && int.TryParse(parts[0], out var singleLine))
            return LineRange.SingleLine(singleLine);

        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var start) &&
            int.TryParse(parts[1], out var end))
        {
            return new LineRange(start, end);
        }

        return LineRange.Empty;
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

        if (fragment.StartsWith('#'))
            fragment = fragment[1..];

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
