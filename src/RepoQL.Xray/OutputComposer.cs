using System.Text;

namespace RepoQL.Xray;

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
    /// <returns>The composed output string.</returns>
    public static string Compose(
        DecisionResult decisionResult,
        bool showConfidence,
        IndexerStatus? indexerStatus = null)
    {
        if (decisionResult.Decisions.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var previousWasMultiline = false;

        for (var i = 0; i < decisionResult.Decisions.Count; i++)
        {
            var decision = decisionResult.Decisions[i];
            var formatted = RepresentationFormatter.Format(decision, showConfidence);
            var isMultiline = IsMultiline(decision);

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
            sb.Append(RepresentationFormatter.FormatStatusFooter(indexerStatus));
        }

        return sb.ToString();
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
}
