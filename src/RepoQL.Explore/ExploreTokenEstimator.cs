using CoreTokenEstimator = RepoQL.Contracts.TokenEstimator;

namespace RepoQL.Explore;

/// <summary>
/// Estimates token counts for XRay representation levels.
///
/// Purpose: Provides accurate token counts for budget-based output rendering decisions.
/// Complexity: Base tokenization is handled by Contracts.TokenEstimator; this class adds
/// representation-level estimation methods specific to XRay rendering.
/// </summary>
public static class ExploreTokenEstimator
{
    /// <summary>
    /// Estimate tokens for Minimal representation (uri + headline).
    /// </summary>
    public static int EstimateMinimal(ExploreResult result)
    {
        // Headline + minimal overhead + approximate URI cost
        return CoreTokenEstimator.EstimateTokens(result.Headline) + 1 + 15;
    }

    /// <summary>
    /// Estimate tokens for Compact representation (uri + headline).
    /// Note: Children are estimated separately via value-based allocation.
    /// </summary>
    public static int EstimateCompact(ExploreResult result, bool showConfidence = true)
    {
        // confidence (if shown) + uri + newline + headline + overhead
        var tokens = 0;
        if (showConfidence)
            tokens += 2; // confidence "XX% "
        tokens += CoreTokenEstimator.EstimateTokens(result.Uri);
        tokens += 1; // newline
        tokens += CoreTokenEstimator.EstimateTokens(result.Headline);
        tokens += 2; // overhead
        return tokens;
    }

    /// <summary>
    /// Estimate tokens for Standard representation (uri + headline + structure).
    /// Note: Children are estimated separately via value-based allocation.
    /// </summary>
    public static int EstimateStandard(ExploreResult result, bool showConfidence = true)
    {
        // Base: confidence + uri + headline + overhead
        var tokens = 0;
        if (showConfidence)
            tokens += 2; // confidence "XX% "
        tokens += CoreTokenEstimator.EstimateTokens(result.Uri);
        tokens += 1; // newline
        tokens += CoreTokenEstimator.EstimateTokens(result.Headline);
        if (!string.IsNullOrWhiteSpace(result.Provenance))
            tokens += 12; // " (semantic)"-style provenance tag
        tokens += 2; // overhead
        tokens += 1; // newline before structure
        tokens += CoreTokenEstimator.EstimateTokens(result.Structure);
        return tokens;
    }

    /// <summary>
    /// Estimate tokens for Rich representation (uri + snippet).
    /// Note: Children are estimated separately via value-based allocation.
    /// </summary>
    public static int EstimateRich(ExploreResult result, bool showConfidence = true)
    {
        // confidence + uri + code fence + snippet + code fence + overhead
        var tokens = 0;
        if (showConfidence)
            tokens += 2; // confidence "XX% "
        tokens += CoreTokenEstimator.EstimateTokens(result.Uri);
        if (!string.IsNullOrWhiteSpace(result.Provenance))
            tokens += 12; // " (semantic)"-style provenance tag
        tokens += 2; // newline + code fence opener
        tokens += CoreTokenEstimator.EstimateTokens(result.Lang); // language hint
        tokens += 1; // newline
        tokens += CoreTokenEstimator.EstimateTokens(result.Snippet);
        tokens += 2; // code fence closer + trailing newline
        return tokens;
    }

    /// <summary>
    /// Estimate tokens for a result at a given representation level.
    /// </summary>
    public static int Estimate(ExploreResult result, Representation level, bool showConfidence = true)
    {
        return level switch
        {
            Representation.Minimal => EstimateMinimal(result),
            Representation.Compact => EstimateCompact(result, showConfidence),
            Representation.Standard => EstimateStandard(result, showConfidence),
            Representation.Rich => EstimateRich(result, showConfidence),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown representation level")
        };
    }

    /// <summary>
    /// Estimate tokens for truncation summary.
    /// </summary>
    public static int EstimateTruncationSummary(bool hasConfidence)
    {
        // "... and N more (X%-Y%)" or "... and N more"
        return hasConfidence ? 8 : 4;
    }
}
