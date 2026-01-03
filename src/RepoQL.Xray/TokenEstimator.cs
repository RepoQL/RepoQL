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
    /// Estimate tokens for Compact representation (uri + headline + children).
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

        // Recursively estimate child objects
        tokens += EstimateChildObjects(result, Representation.Compact);

        return tokens;
    }

    /// <summary>
    /// Estimate tokens for Standard representation (uri + headline + structure + children).
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

        // Recursively estimate child objects
        tokens += EstimateChildObjects(result, Representation.Standard);

        return tokens;
    }

    /// <summary>
    /// Estimate tokens for Rich representation (uri + snippet + children).
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

        // Recursively estimate child objects
        tokens += EstimateChildObjects(result, Representation.Rich);

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

    /// <summary>
    /// Estimate tokens for child objects at a given representation level.
    /// Mirrors RepresentationFormatter.AppendChildObjects which indents each child.
    /// </summary>
    private static int EstimateChildObjects(XrayResult result, Representation level)
    {
        if (result.ChildObjects is null || result.ChildObjects.Count == 0)
            return 0;

        var tokens = 0;
        foreach (var child in result.ChildObjects)
        {
            tokens += 1; // newline before child
            tokens += Estimate(child, level);
            // Indentation adds ~0.5 tokens per line (2 spaces), but varies by content.
            // Rough estimate: 1 token per child for indentation overhead
            tokens += 1;
        }

        return tokens;
    }
}
