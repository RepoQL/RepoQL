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
    /// <param name="trustSignal">Optional trust signal for footer.</param>
    /// <param name="intent">Optional intent that controls headline density.</param>
    /// <returns>The composed output string.</returns>
    public static string Compose(
        DecisionResult decisionResult,
        bool showConfidence,
        TrustSignal? trustSignal = null,
        Intent? intent = null)
    {
        if (decisionResult.Decisions.Count == 0)
            return string.Empty;

        var clusteredOutput = ResultClusterer.Cluster([.. decisionResult.Decisions]);
        var sb = new StringBuilder();
        var previousWasMultiline = false;
        var previousWasHeader = false;
        int? previousConfidence = null;
        var insertedConfidenceSeparator = false;
        var hasOutput = false;

        foreach (var item in clusteredOutput.Items)
        {
            if (item is ClusterHeader header)
            {
                if (hasOutput)
                {
                    sb.Append('\n');
                    if (previousWasMultiline)
                        sb.Append('\n');
                }

                sb.Append(FormatClusterHeader(header));
                previousWasMultiline = false;
                previousWasHeader = true;
                hasOutput = true;
                continue;
            }

            var decision = (RenderingDecision)item;
            var formatted = FormatWithChildren(decision, showConfidence, indent: 0, parentUri: null, intent);
            var isMultiline = IsMultiline(decision) || decision.ChildDecisions is { Count: > 0 };
            var needsConfidenceSeparator =
                showConfidence
                && !insertedConfidenceSeparator
                && previousConfidence.HasValue
                && previousConfidence.Value - decision.Result.Confidence > 30;

            if (hasOutput)
            {
                // Keep header -> member transitions tight, but preserve multiline spacing elsewhere.
                if (!previousWasHeader && (previousWasMultiline || isMultiline))
                    sb.Append('\n');

                if (needsConfidenceSeparator)
                    sb.Append('\n');

                sb.Append('\n');
            }

            sb.Append(formatted);
            previousWasMultiline = isMultiline;
            previousWasHeader = false;
            previousConfidence = decision.Result.Confidence;
            if (needsConfidenceSeparator)
                insertedConfidenceSeparator = true;
            hasOutput = true;
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
        if (trustSignal is not null)
        {
            sb.Append('\n');
            if (previousWasMultiline || decisionResult.OmittedCount > 0)
                sb.Append('\n');

            var totalTokens = CalculateTotalTokens(clusteredOutput.Items);
            sb.Append(RepresentationFormatter.FormatStatusFooter(trustSignal, totalTokens));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Calculate total tokens for all output items.
    /// </summary>
    private static int CalculateTotalTokens(IReadOnlyList<OutputItem> items)
    {
        var total = 0;
        foreach (var item in items)
        {
            switch (item)
            {
                case ClusterHeader:
                    total += ResultClusterer.ClusterHeaderTokenCost;
                    break;
                case RenderingDecision decision:
                    total += CalculateDecisionTokens(decision);
                    break;
            }
        }
        return total;
    }

    private static int CalculateDecisionTokens(RenderingDecision decision)
    {
        var total = decision.EstimatedTokens;
        if (decision.ChildDecisions is not { Count: > 0 })
            return total;

        foreach (var child in decision.ChildDecisions)
            total += CalculateDecisionTokens(child);

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
    /// <param name="intent">Optional intent that controls headline density.</param>
    private static string FormatWithChildren(RenderingDecision decision, bool showConfidence, int indent, string? parentUri, Intent? intent = null)
    {
        var sb = new StringBuilder();
        var indentStr = new string(' ', indent * 2);

        // Format this decision at its assigned level
        var formatted = RepresentationFormatter.Format(decision, showConfidence, parentUri, intent);

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
                sb.Append(FormatWithChildren(child, showConfidence, indent + 1, thisUri, intent));
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

    private static string FormatClusterHeader(ClusterHeader header)
    {
        var noun = header.MemberCount == 1 ? "result" : "results";
        return $"── {header.SharedPath} ({header.MemberCount} {noun}) ──";
    }
}
