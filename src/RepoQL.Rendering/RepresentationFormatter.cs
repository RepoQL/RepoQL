using System.Text;

namespace RepoQL.Rendering;

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
    /// Format a result at Compact level (uri + headline on single line).
    /// </summary>
    public static string FormatCompact(XrayResult result, bool showConfidence)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence);

        var headline = GetSingleLineHeadline(result);
        if (headline != null)
        {
            sb.Append('\n');
            sb.Append(headline);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a result at Standard level (uri + headline + structure).
    /// </summary>
    public static string FormatStandard(XrayResult result, bool showConfidence)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, result, showConfidence);

        var headline = GetSingleLineHeadline(result);
        if (headline != null)
        {
            sb.Append('\n');
            sb.Append(headline);
        }

        if (!string.IsNullOrEmpty(result.Structure))
        {
            sb.Append('\n');
            sb.Append(result.Structure);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a result at Rich level (uri + snippet, no headline).
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
    /// Format a truncation summary with semantic type breakdown and indexer status.
    /// </summary>
    public static string FormatTruncationSummary(
        int count,
        IReadOnlyDictionary<string, int>? omittedByType,
        IndexerStatus? indexerStatus)
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

        // Add indexer status if not complete
        if (indexerStatus is not null && indexerStatus.Stage != "Complete")
        {
            sb.Append('\n');
            sb.Append("[Indexer: ");
            sb.Append(indexerStatus.Stage);

            if (indexerStatus.Progress.HasValue)
                sb.Append($" {indexerStatus.Progress}%");

            if (indexerStatus.PendingFiles.HasValue && indexerStatus.PendingFiles > 0)
                sb.Append($", {indexerStatus.PendingFiles} pending");

            sb.Append(']');
        }

        return sb.ToString();
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
