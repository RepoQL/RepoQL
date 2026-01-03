namespace RepoQL.Xray;

/// <summary>
/// Estimates token counts for text and representation levels.
/// Uses a simple heuristic (chars/4) that can be upgraded to a real tokenizer later.
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// Estimate tokens for a string using chars/4 heuristic.
    /// </summary>
    public static int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>
    /// Estimate tokens for Minimal representation (headline only).
    /// </summary>
    public static int EstimateMinimal(XrayResult result)
    {
        // Just headline (single line) + minimal overhead
        return EstimateTokens(result.Headline) + 1;
    }

    /// <summary>
    /// Estimate tokens for Compact representation (uri + headline).
    /// Note: Children are estimated separately via value-based allocation.
    /// </summary>
    public static int EstimateCompact(XrayResult result)
    {
        // confidence (if shown) + kind badge + uri + newline + headline + overhead
        var tokens = 0;
        tokens += 2; // confidence "XX% "
        if (result.Kind != null)
            tokens += EstimateTokens($"[{result.Kind}] ");
        tokens += EstimateTokens(result.Uri);
        tokens += 1; // newline
        tokens += EstimateTokens(result.Headline);
        tokens += 2; // overhead
        return tokens;
    }

    /// <summary>
    /// Estimate tokens for Standard representation (uri + headline + structure).
    /// Note: Children are estimated separately via value-based allocation.
    /// </summary>
    public static int EstimateStandard(XrayResult result)
    {
        // Base: confidence + kind badge + uri + headline + overhead
        var tokens = 0;
        tokens += 2; // confidence "XX% "
        if (result.Kind != null)
            tokens += EstimateTokens($"[{result.Kind}] ");
        tokens += EstimateTokens(result.Uri);
        tokens += 1; // newline
        tokens += EstimateTokens(result.Headline);
        tokens += 2; // overhead
        tokens += 1; // newline before structure
        tokens += EstimateTokens(result.Structure);
        return tokens;
    }

    /// <summary>
    /// Estimate tokens for Rich representation (uri + snippet).
    /// Note: Children are estimated separately via value-based allocation.
    /// </summary>
    public static int EstimateRich(XrayResult result)
    {
        // confidence + kind badge + uri + code fence + snippet + code fence + overhead
        var tokens = 0;
        tokens += 2; // confidence "XX% "
        if (result.Kind != null)
            tokens += EstimateTokens($"[{result.Kind}] ");
        tokens += EstimateTokens(result.Uri);
        tokens += 2; // newline + code fence opener
        tokens += EstimateTokens(result.Lang); // language hint
        tokens += 1; // newline
        tokens += EstimateTokens(result.Snippet);
        tokens += 2; // code fence closer + trailing newline
        return tokens;
    }

    /// <summary>
    /// Estimate tokens for a result at a given representation level.
    /// </summary>
    public static int Estimate(XrayResult result, Representation level)
    {
        return level switch
        {
            Representation.Minimal => EstimateMinimal(result),
            Representation.Compact => EstimateCompact(result),
            Representation.Standard => EstimateStandard(result),
            Representation.Rich => EstimateRich(result),
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
