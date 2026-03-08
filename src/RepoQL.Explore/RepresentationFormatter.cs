using System.Text;
using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Formats results at different representation levels.
/// </summary>
public static class RepresentationFormatter
{
    /// <summary>
    /// Format a result at Minimal level (uri + headline).
    /// Used for wide Explore results without search criteria.
    /// </summary>
    public static string FormatMinimal(ExploreResult result)
    {
        var headline = GetHeadline(result, result.Uri) ?? ExtractFileName(result.Uri);
        return $"{result.Uri}  {headline}";
    }

    /// <summary>
    /// Format a result at Compact level (uri + headline on same line).
    /// Note: Children are now handled separately via nested decisions in OutputComposer.
    /// </summary>
    public static string FormatCompact(ExploreResult result, bool showConfidence, string? parentUri = null)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence, parentUri, includeProvenance: false);

        // For files (non-children), append headline on same line after URI
        // Children already have headline in header
        var (_, isChild) = GetDisplayUri(result.Uri, parentUri);
        if (!isChild)
        {
            var headline = GetHeadline(result, result.Uri);
            if (headline != null)
            {
                sb.Append("  ");
                sb.Append(headline);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a result at Standard level (uri + headline + structure).
    /// Note: Children are now handled separately via nested decisions in OutputComposer.
    /// </summary>
    public static string FormatStandard(ExploreResult result, bool showConfidence, string? parentUri = null)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence, parentUri, includeProvenance: true);

        // Alignment prefix for continuation lines when confidence is shown
        var alignPrefix = showConfidence ? "  " : "";

        // Don't repeat headline for children - it's already in the header
        var (_, isChild) = GetDisplayUri(result.Uri, parentUri);
        if (!isChild)
        {
            var headline = GetHeadline(result, result.Uri);
            if (headline != null)
            {
                sb.Append("  ");
                sb.Append(headline);
                AppendProvenance(sb, result.Provenance);
            }
        }

        if (!string.IsNullOrEmpty(result.Structure))
        {
            sb.Append('\n');
            // Apply alignment to each line of structure
            var structureLines = result.Structure.Split('\n');
            for (var i = 0; i < structureLines.Length; i++)
            {
                if (i > 0)
                    sb.Append('\n');
                sb.Append(alignPrefix);
                sb.Append(structureLines[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a result at Rich level (uri + snippet).
    /// Note: Children are now handled separately via nested decisions in OutputComposer.
    /// </summary>
    public static string FormatRich(ExploreResult result, bool showConfidence, string? parentUri = null)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence, parentUri, includeProvenance: true);

        var (_, isChild) = GetDisplayUri(result.Uri, parentUri);
        if (!isChild)
            AppendProvenance(sb, result.Provenance);

        if (!string.IsNullOrEmpty(result.Snippet))
        {
            // Skip snippet if it just repeats the headline — no information gained.
            var headline = GetSingleLineHeadline(result);
            var isRedundant = headline != null
                && result.Snippet.TrimEnd() == headline.TrimEnd();

            if (!isRedundant && result.Snippet.Contains('\n'))
            {
                // Multi-line snippet: code fence
                sb.Append('\n');
                sb.Append("```");
                if (!string.IsNullOrEmpty(result.Lang))
                    sb.Append(result.Lang);
                sb.Append('\n');
                sb.Append(result.Snippet);
                if (!result.Snippet.EndsWith('\n'))
                    sb.Append('\n');
                sb.Append("```");
            }
            else if (!isRedundant)
            {
                // Single-line snippet with new information: inline
                sb.Append('\n');
                sb.Append("  ");
                sb.Append(result.Snippet.TrimEnd());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a decision at the appropriate level.
    /// </summary>
    /// <param name="decision">The rendering decision to format.</param>
    /// <param name="showConfidence">Whether to show confidence scores.</param>
    /// <param name="parentUri">If provided, child URIs will display only the fragment portion.</param>
    public static string Format(RenderingDecision decision, bool showConfidence, string? parentUri = null)
    {
        return decision.Level switch
        {
            Representation.Minimal => FormatMinimal(decision.Result),
            Representation.Compact => FormatCompact(decision.Result, showConfidence, parentUri),
            Representation.Standard => FormatStandard(decision.Result, showConfidence, parentUri),
            Representation.Rich => FormatRich(decision.Result, showConfidence, parentUri),
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };
    }

    /// <summary>
    /// Format a truncation summary with semantic type breakdown.
    /// </summary>
    public static string FormatTruncationSummary(
        int count,
        IReadOnlyDictionary<string, int>? omittedByType)
    {
        var sb = new StringBuilder();

        // Format: [More: 25x markdown.doc, 50x code.csharp]
        sb.Append("[More: ");

        if (omittedByType is { Count: > 0 })
        {
            var parts = omittedByType
                .Take(4)  // Limit to top 4 types
                .Select(kvp => $"{kvp.Value}x {kvp.Key}");
            sb.Append(string.Join(", ", parts));

            var remaining = omittedByType.Skip(4).Sum(kvp => kvp.Value);
            if (remaining > 0)
                sb.Append($", +{remaining} other");
        }
        else
        {
            sb.Append(count);
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Format an enhanced truncation summary with hints.
    /// </summary>
    /// <param name="omittedDocuments">Number of documents omitted.</param>
    /// <param name="omittedObjects">Number of objects (symbols) omitted.</param>
    /// <param name="omittedByType">Breakdown by semantic type.</param>
    /// <param name="hints">Action hints for the user.</param>
    /// <returns>Formatted summary string.</returns>
    public static string FormatEnhancedTruncationSummary(
        int omittedDocuments,
        int omittedObjects,
        IReadOnlyDictionary<string, int>? omittedByType,
        IReadOnlyList<string>? hints)
    {
        var sb = new StringBuilder();

        // Format: [More: 3 docs, 8 symbols (5x csharp.method, 3x csharp.class) | narrow with pattern]
        sb.Append("[More: ");

        var parts = new List<string>();
        if (omittedDocuments > 0)
            parts.Add($"{omittedDocuments} doc{(omittedDocuments > 1 ? "s" : "")}");
        if (omittedObjects > 0)
            parts.Add($"{omittedObjects} symbol{(omittedObjects > 1 ? "s" : "")}");

        if (parts.Count > 0)
        {
            sb.Append(string.Join(", ", parts));

            // Add type breakdown if available
            if (omittedByType is { Count: > 0 })
            {
                var typeBreakdown = omittedByType
                    .Take(3)
                    .Select(kvp => $"{kvp.Value}x {kvp.Key}");
                sb.Append(" (");
                sb.Append(string.Join(", ", typeBreakdown));
                sb.Append(')');
            }
        }
        else
        {
            sb.Append("more results available");
        }

        // Add hints if available
        if (hints is { Count: > 0 })
        {
            sb.Append(" | ");
            sb.Append(string.Join(", ", hints));
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Format the status footer showing indexer state, timing, and token usage.
    /// </summary>
    /// <param name="status">Current trust signal.</param>
    /// <param name="tokenCount">Optional token count for the output.</param>
    /// <param name="representationHint">Optional representation hint (inner content) to append.</param>
    public static string FormatStatusFooter(TrustSignal status, int? tokenCount = null, string? representationHint = null)
    {
        if (status.IndexTotal == status.IndexPending && status.IndexPending > 0)
            return $"[NOT READY - {status.IndexPending} pending, discovery in progress]";

        string indexStatus;
        if (status.IndexPending == 0 && status.IndexFailed == 0 && status.IndexStale == 0)
        {
            indexStatus = "ready";
        }
        else if (status.IndexPending > 0)
        {
            if (status.IndexTotal > 0)
            {
                var indexed = Math.Max(0, status.IndexTotal - status.IndexPending);
                var percent = (indexed * 100) / status.IndexTotal;
                indexStatus = $"{percent}% ({status.IndexPending} pending)";
            }
            else
            {
                // Fallback when total is unavailable (legacy hosts / degraded status computation).
                indexStatus = $"{status.IndexPending} pending";
            }
        }
        else
        {
            indexStatus = "ready";
        }

        var semanticStatus = !status.SemanticEnabled
            ? "disabled"
            : status.SemanticReady
                ? "ready"
                : status.IndexTotal <= 0
                    ? "pending"
                    : $"{Math.Clamp(status.SemanticPercent, 0, 100)}%";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(status.SearchQualityTier))
            parts.Add($"quality: {status.SearchQualityTier}");

        if (status.CoverageAboveThreshold.HasValue && status.CoverageTotalDocuments.HasValue)
        {
            var above = Math.Max(0, status.CoverageAboveThreshold.Value);
            var total = Math.Max(0, status.CoverageTotalDocuments.Value);
            if (status.CoverageAllInScope)
                parts.Add($"{above} matches (all in scope)");
            else
                parts.Add($"{above} of {total} above threshold");
        }

        if (tokenCount.HasValue)
            parts.Add(FormatTokenCount(tokenCount.Value));

        parts.Add(FormatDuration(status.ExecutionTimeMs));
        parts.Add($"index: {indexStatus}");
        parts.Add($"semantic: {semanticStatus}");

        if (status.IndexFailed > 0)
            parts.Add($"{status.IndexFailed} failed");
        if (status.IndexStale > 0)
            parts.Add($"stale: {status.IndexStale}");
        if (!string.IsNullOrEmpty(representationHint))
            parts.Add(representationHint);

        return $"[{string.Join(" | ", parts)}]";
    }

    /// <summary>
    /// Format a hint about the representation level chosen and what budget is needed for higher-fidelity representations.
    /// Returns the inner content (without brackets) to be appended to the footer, or null when not needed.
    /// </summary>
    /// <param name="level">The representation level chosen ("full", "structure", "headline", "none").</param>
    /// <param name="costs">Token costs for each representation level.</param>
    /// <returns>Inner hint content (pipe-delimited), or null if no hint is needed.</returns>
    public static string? FormatRepresentationHint(string level, RepresentationCosts costs)
    {
        if (level == "full")
            return null;

        var parts = new List<string>();
        parts.Add($"showing: {level}");

        // Show costs for higher-fidelity representations
        if (level == "headline")
        {
            if (costs.StructureTokens.HasValue)
                parts.Add($"structure: {FormatTokenCount(costs.StructureTokens.Value)}");
            if (costs.FullTokens.HasValue)
                parts.Add($"full: {FormatTokenCount(costs.FullTokens.Value)}");
        }
        else if (level == "structure")
        {
            if (costs.FullTokens.HasValue)
                parts.Add($"full: {FormatTokenCount(costs.FullTokens.Value)}");
        }
        else if (level == "none")
        {
            // Show what's available (if anything)
            if (costs.HeadlineTokens.HasValue)
                parts.Add($"headline: {FormatTokenCount(costs.HeadlineTokens.Value)}");
            if (costs.StructureTokens.HasValue)
                parts.Add($"structure: {FormatTokenCount(costs.StructureTokens.Value)}");
            if (costs.FullTokens.HasValue)
                parts.Add($"full: {FormatTokenCount(costs.FullTokens.Value)}");
        }

        // If we only have the level, no additional info to show
        if (parts.Count == 1)
            return null;

        // Return inner content without brackets - caller will integrate into footer
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Format token count as a human-readable quantity (e.g., "1.5k tok").
    /// </summary>
    private static string FormatTokenCount(int tokens)
    {
        return tokens switch
        {
            < 1000 => $"{tokens} tok",
            < 10000 => $"{tokens / 1000.0:F1}k tok",
            _ => $"{tokens / 1000.0:F0}k tok"
        };
    }

    /// <summary>
    /// Format duration as a concise human-readable string (e.g., "1.2 s", "150 ms").
    /// </summary>
    private static string FormatDuration(long milliseconds)
    {
        return milliseconds switch
        {
            < 1000 => $"{milliseconds} ms",
            < 10000 => $"{milliseconds / 1000.0:F1} s",
            < 60000 => $"{milliseconds / 1000.0:F0} s",
            < 3600000 => $"{milliseconds / 60000.0:F1} min",
            _ => $"{milliseconds / 3600000.0:F1} hr"
        };
    }

    /// <summary>
    /// Append the header line: [confidence] [kind] uri
    /// When parentUri is provided and result is a child, shows headline before the fragment (no kind badge).
    /// </summary>
    private static void AppendHeader(
        StringBuilder sb,
        ExploreResult result,
        bool showConfidence,
        string? parentUri = null,
        bool includeProvenance = false)
    {
        if (showConfidence)
            sb.Append($"{result.Confidence,3}% ");

        var (displayUri, isChild) = GetDisplayUri(result.Uri, parentUri);

        if (isChild)
        {
            // For children: show headline first, then fragment
            var headline = GetHeadline(result);
            if (!string.IsNullOrEmpty(headline))
            {
                sb.Append(headline);
                if (includeProvenance)
                    AppendProvenance(sb, result.Provenance);
                sb.Append(' ');
            }
        }

        sb.Append(displayUri);
    }

    /// <summary>
    /// Get the URI to display. When parentUri is provided and the result URI is a child
    /// (same base with fragment), returns only the fragment portion.
    /// </summary>
    /// <returns>A tuple of (displayUri, isChild) where isChild indicates if this is a child of the parent.</returns>
    private static (string displayUri, bool isChild) GetDisplayUri(string uri, string? parentUri)
    {
        if (string.IsNullOrEmpty(parentUri))
            return (uri, false);

        // Extract fragment from the URI
        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex < 0)
            return (uri, false); // No fragment, show full URI

        var baseUri = uri[..hashIndex];

        // Check if this is a child of the parent (same base URI)
        var parentHashIndex = parentUri.IndexOf('#', StringComparison.Ordinal);
        var parentBaseUri = parentHashIndex >= 0 ? parentUri[..parentHashIndex] : parentUri;

        if (baseUri.Equals(parentBaseUri, StringComparison.OrdinalIgnoreCase))
        {
            // Same file - show only the fragment
            return (CompactChildFragment(uri[hashIndex..]), true);
        }

        return (uri, false);
    }

    private static string? GetHeadline(ExploreResult result, string? uri = null)
    {
        var headline = GetSingleLineHeadline(result);
        if (headline is null || uri is null)
            return headline;

        // Strip redundant filename prefix: "foo.cs | Type | ..." → "Type | ..."
        // when the URI already shows the filename.
        var pipeIndex = headline.IndexOf(" | ", StringComparison.Ordinal);
        if (pipeIndex <= 0)
            return headline;

        var prefix = headline[..pipeIndex];
        var fileName = ExtractFileName(uri);
        if (string.Equals(prefix, fileName, StringComparison.OrdinalIgnoreCase))
            return headline[(pipeIndex + 3)..];

        return headline;
    }

    /// <summary>
    /// Get headline as a single line (truncate at first newline).
    /// </summary>
    private static string? GetSingleLineHeadline(ExploreResult result)
    {
        if (string.IsNullOrEmpty(result.Headline))
            return null;

        var newlineIndex = result.Headline.IndexOf('\n', StringComparison.Ordinal);
        return newlineIndex >= 0
            ? result.Headline[..newlineIndex].TrimEnd()
            : result.Headline;
    }

    /// <summary>
    /// Extract the short description from an x-ray headline.
    /// X-ray headlines use pipe-delimited format: "Description | type | size | tokens | sections".
    /// Returns just the description.
    /// </summary>
    public static string? ShortHeadline(string? headline)
    {
        if (string.IsNullOrWhiteSpace(headline))
            return null;

        var pipeIndex = headline.IndexOf(" | ", StringComparison.Ordinal);
        return pipeIndex >= 0 ? headline[..pipeIndex].TrimEnd() : headline;
    }

    /// <summary>
    /// Extract filename from URI as fallback for Minimal format.
    /// </summary>
    private static string ExtractFileName(string uri)
    {
        var trimmed = uri.TrimEnd('/');
        // Remove fragment
        var hashIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
            trimmed = trimmed[..hashIndex];
        // Get last segment
        var slashIndex = trimmed.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < trimmed.Length - 1
            ? trimmed[(slashIndex + 1)..]
            : trimmed;
    }

    private static void AppendProvenance(StringBuilder sb, string? provenance)
    {
        if (string.IsNullOrWhiteSpace(provenance))
            return;

        sb.Append(" (");
        sb.Append(provenance);
        sb.Append(')');
    }

    private static string CompactChildFragment(string fragment)
    {
        if (!fragment.Contains("symbol=", StringComparison.Ordinal))
            return fragment;

        var symbolValue = ExtractFragmentParameter(fragment, "symbol");
        if (string.IsNullOrWhiteSpace(symbolValue))
            return fragment;

        var simpleNameIndex = symbolValue.LastIndexOf('.');
        var simpleName = simpleNameIndex >= 0 && simpleNameIndex < symbolValue.Length - 1
            ? symbolValue[(simpleNameIndex + 1)..]
            : symbolValue;

        return $"#symbol={simpleName}";
    }

    private static string? ExtractFragmentParameter(string fragment, string key)
    {
        var query = fragment.StartsWith('#') ? fragment[1..] : fragment;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex < 0)
                continue;

            var pairKey = pair[..equalsIndex];
            if (!pairKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return pair[(equalsIndex + 1)..];
        }

        return null;
    }
}
