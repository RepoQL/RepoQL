using System.Text;

namespace RepoQL.Xray;

/// <summary>
/// Formats results at different representation levels.
/// </summary>
public static class RepresentationFormatter
{
    /// <summary>
    /// Format a result at Minimal level (headline only, no URI).
    /// Used for wide Explore results without search criteria.
    /// </summary>
    public static string FormatMinimal(XrayResult result)
    {
        // Just the headline (first line only), or filename as fallback
        var headline = GetSingleLineHeadline(result);
        return headline ?? ExtractFileName(result.Uri);
    }

    /// <summary>
    /// Format a result at Compact level (uri + headline).
    /// Note: Children are now handled separately via nested decisions in OutputComposer.
    /// </summary>
    public static string FormatCompact(XrayResult result, bool showConfidence)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence);

        var headline = GetSingleLineHeadline(result);
        if (headline != null)
        {
            sb.Append('\n');
            // Align with content after confidence score (5 chars: "100% ")
            if (showConfidence)
                sb.Append("  ");
            sb.Append(headline);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a result at Standard level (uri + headline + structure).
    /// Note: Children are now handled separately via nested decisions in OutputComposer.
    /// </summary>
    public static string FormatStandard(XrayResult result, bool showConfidence)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence);

        // Alignment prefix for continuation lines when confidence is shown
        var alignPrefix = showConfidence ? "  " : "";

        var headline = GetSingleLineHeadline(result);
        if (headline != null)
        {
            sb.Append('\n');
            sb.Append(alignPrefix);
            sb.Append(headline);
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
    public static string FormatRich(XrayResult result, bool showConfidence)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence);

        if (!string.IsNullOrEmpty(result.Snippet))
        {
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

        return sb.ToString();
    }

    /// <summary>
    /// Format a decision at the appropriate level.
    /// </summary>
    public static string Format(RenderingDecision decision, bool showConfidence)
    {
        return decision.Level switch
        {
            Representation.Minimal => FormatMinimal(decision.Result),
            Representation.Compact => FormatCompact(decision.Result, showConfidence),
            Representation.Standard => FormatStandard(decision.Result, showConfidence),
            Representation.Rich => FormatRich(decision.Result, showConfidence),
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
    /// <param name="status">Current indexer status.</param>
    /// <param name="tokenCount">Optional token count for the output.</param>
    /// <param name="representationHint">Optional representation hint (inner content) to append.</param>
    public static string FormatStatusFooter(IndexerStatus status, int? tokenCount = null, string? representationHint = null)
    {
        // Format: [1.5k tok | 42ms | index: ready | semantic: ready]
        // Or if busy: [1.2k tok | 42ms | index: 5 pending | semantic: pending]
        // Or with hint: [1.2k tok | 42ms | index: ready | semantic: ready | showing: structure | full: 5.2k tok]
        var indexStatus = status.IndexPending > 0
            ? $"{status.IndexPending} pending"
            : "ready";

        string semanticStatus;
        if (!status.SemanticEnabled)
            semanticStatus = "disabled";
        else if (status.SemanticReady)
            semanticStatus = "ready";
        else
            semanticStatus = "pending";

        var tokenPart = tokenCount.HasValue
            ? $"{FormatTokenCount(tokenCount.Value)} | "
            : "";

        var duration = FormatDuration(status.ElapsedMs);

        var hintPart = !string.IsNullOrEmpty(representationHint)
            ? $" | {representationHint}"
            : "";

        return $"[{tokenPart}{duration} | index: {indexStatus} | semantic: {semanticStatus}{hintPart}]";
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
    /// </summary>
    private static void AppendHeader(StringBuilder sb, XrayResult result, bool showConfidence)
    {
        if (showConfidence)
            sb.Append($"{result.Confidence,3}% ");

        if (!string.IsNullOrEmpty(result.Kind))
            sb.Append('[').Append(result.Kind).Append("] ");

        sb.Append(result.Uri);
    }

    /// <summary>
    /// Get headline as a single line (truncate at first newline).
    /// </summary>
    private static string? GetSingleLineHeadline(XrayResult result)
    {
        if (string.IsNullOrEmpty(result.Headline))
            return null;

        var newlineIndex = result.Headline.IndexOf('\n');
        return newlineIndex >= 0
            ? result.Headline[..newlineIndex].TrimEnd()
            : result.Headline;
    }

    /// <summary>
    /// Extract filename from URI as fallback for Minimal format.
    /// </summary>
    private static string ExtractFileName(string uri)
    {
        var trimmed = uri.TrimEnd('/');
        // Remove fragment
        var hashIndex = trimmed.IndexOf('#');
        if (hashIndex >= 0)
            trimmed = trimmed[..hashIndex];
        // Get last segment
        var slashIndex = trimmed.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < trimmed.Length - 1
            ? trimmed[(slashIndex + 1)..]
            : trimmed;
    }
}
