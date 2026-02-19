using System.Text;

namespace RepoQL.Explore;

/// <summary>
/// Composes final output from rendering decisions.
/// </summary>
public static class OutputComposer
{
    /// <summary>
    /// Compose the final output string.
    /// </summary>
    /// <param name="decisionResult">The decision result containing decisions and omitted info.</param>
    /// <param name="showConfidence">Whether to show confidence scores.</param>
    /// <param name="indexerStatus">Optional indexer status for footer.</param>
    /// <param name="intent">Optional intent — Inspect uses short headlines.</param>
    /// <returns>The composed output string.</returns>
    public static string Compose(
        DecisionResult decisionResult,
        bool showConfidence,
        IndexerStatus? indexerStatus = null,
        Intent? intent = null)
    {
        if (decisionResult.Decisions.Count == 0)
            return string.Empty;

        var useShortHeadlines = intent == Intent.Inspect;
        var sb = new StringBuilder();
        var previousWasMultiline = false;

        for (var i = 0; i < decisionResult.Decisions.Count; i++)
        {
            var decision = decisionResult.Decisions[i];
            var formatted = FormatWithChildren(decision, showConfidence, indent: 0, parentUri: null, useShortHeadlines);
            var isMultiline = IsMultiline(decision) || decision.ChildDecisions is { Count: > 0 };

            // Add blank line before multi-line items (except first)
            if (i > 0 && (previousWasMultiline || isMultiline))
                sb.Append('\n');

            if (i > 0)
                sb.Append('\n');

            sb.Append(formatted);
            previousWasMultiline = isMultiline;
        }

        // Add truncation summary if items were omitted
        if (decisionResult.OmittedCount > 0)
        {
            sb.Append('\n');
            if (previousWasMultiline)
                sb.Append('\n');
            sb.Append(RepresentationFormatter.FormatTruncationSummary(
                decisionResult.OmittedCount,
                decisionResult.OmittedByType));
        }

        // Always add status footer if available
        if (indexerStatus is not null)
        {
            sb.Append('\n');
            if (previousWasMultiline || decisionResult.OmittedCount > 0)
                sb.Append('\n');

            var totalTokens = CalculateTotalTokens(decisionResult.Decisions);
            sb.Append(RepresentationFormatter.FormatStatusFooter(indexerStatus, totalTokens));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Calculate total tokens for all decisions including children.
    /// </summary>
    private static int CalculateTotalTokens(IReadOnlyList<RenderingDecision> decisions)
    {
        var total = 0;
        foreach (var decision in decisions)
        {
            total += decision.EstimatedTokens;
            if (decision.ChildDecisions is { Count: > 0 })
                total += CalculateTotalTokens(decision.ChildDecisions);
        }
        return total;
    }

    /// <summary>
    /// Determines if a decision will produce multi-line output.
    /// </summary>
    private static bool IsMultiline(RenderingDecision decision)
    {
        // Standard (has structure) and Rich (has snippet) are multi-line
        return decision.Level switch
        {
            Representation.Standard => !string.IsNullOrEmpty(decision.Result.Structure),
            Representation.Rich => !string.IsNullOrEmpty(decision.Result.Snippet),
            _ => false
        };
    }

    /// <summary>
    /// Format a decision and its children recursively, with proper indentation.
    /// </summary>
    /// <param name="decision">The decision to format.</param>
    /// <param name="showConfidence">Whether to show confidence scores.</param>
    /// <param name="indent">Current indentation level.</param>
    /// <param name="parentUri">Parent URI for fragment-only display of children.</param>
    private static string FormatWithChildren(RenderingDecision decision, bool showConfidence, int indent, string? parentUri, bool useShortHeadlines = false)
    {
        var sb = new StringBuilder();
        var indentStr = new string(' ', indent * 2);

        // Format this decision at its assigned level
        var formatted = RepresentationFormatter.Format(decision, showConfidence, parentUri, useShortHeadlines);

        // Apply indentation to each line
        var lines = formatted.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(lines[i]))
                sb.Append(indentStr).Append(lines[i]);
            else if (indent == 0)
                sb.Append(lines[i]); // Preserve empty lines at top level
        }

        // Recursively format children with increased indent, passing this decision's URI as parent
        if (decision.ChildDecisions is { Count: > 0 })
        {
            var thisUri = decision.Result.Uri;
            foreach (var child in decision.ChildDecisions)
            {
                sb.Append('\n');
                sb.Append(FormatWithChildren(child, showConfidence, indent + 1, thisUri, useShortHeadlines));
            }
        }

        // Show omitted children indicator if any were filtered out
        if (decision.OmittedChildrenCount > 0)
        {
            sb.Append('\n');
            var childIndentStr = new string(' ', (indent + 1) * 2);
            sb.Append(childIndentStr);
            sb.Append($"[+{decision.OmittedChildrenCount} more]");
        }

        return sb.ToString();
    }
}
