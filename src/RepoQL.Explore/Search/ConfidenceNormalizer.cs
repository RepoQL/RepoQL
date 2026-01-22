namespace RepoQL.Explore.Search;

/// <summary>
/// Normalizes raw scores to 0-100 confidence values using a hybrid approach:
/// 75% sigmoid curve + 25% floor-based scaling.
/// </summary>
public static class ConfidenceNormalizer
{
    /// <summary>
    /// Sigmoid steepness factor. Higher = sharper transition around midpoint.
    /// </summary>
    private const double SigmoidK = 12.0;

    /// <summary>
    /// Sigmoid midpoint. Scores above this get boosted, below get penalized.
    /// </summary>
    private const double SigmoidMidpoint = 0.50;

    /// <summary>
    /// Floor for hybrid component. Scores below this contribute 0 to hybrid.
    /// </summary>
    private const double HybridFloor = 0.20;

    /// <summary>
    /// Weight for sigmoid component (remainder goes to hybrid).
    /// </summary>
    private const double SigmoidWeight = 0.75;

    /// <summary>
    /// Normalize raw scores to 1-100 confidence range against fixed thresholds.
    /// Recursively normalizes child objects.
    /// </summary>
    public static IReadOnlyList<SearchResult> Normalize(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0)
            return results;

        var normalized = new SearchResult[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            normalized[i] = NormalizeResult(results[i]);
        }

        return normalized;
    }

    /// <summary>
    /// Normalize a single result and its children recursively.
    /// </summary>
    private static SearchResult NormalizeResult(SearchResult result)
    {
        var confidence = ScoreToConfidence(result.RawScore);

        // Recursively normalize child objects if present
        IReadOnlyList<SearchResult>? normalizedChildren = null;
        if (result.ChildObjects is not null && result.ChildObjects.Count > 0)
        {
            var children = new SearchResult[result.ChildObjects.Count];
            for (var i = 0; i < result.ChildObjects.Count; i++)
            {
                children[i] = NormalizeResult(result.ChildObjects[i]);
            }
            normalizedChildren = children;
        }

        return result with
        {
            Confidence = confidence,
            ChildObjects = normalizedChildren
        };
    }

    /// <summary>
    /// Normalize a mutable list in place.
    /// Recursively normalizes child objects.
    /// </summary>
    public static void NormalizeInPlace(IList<SearchResult> results)
    {
        if (results.Count == 0) return;

        for (var i = 0; i < results.Count; i++)
        {
            results[i] = NormalizeResult(results[i]);
        }
    }

    /// <summary>
    /// Convert a raw score to 0-100 confidence using hybrid approach:
    /// sigmoid + floor-based scaling.
    /// </summary>
    public static int ScoreToConfidence(double rawScore)
    {
        // Sigmoid component: smooth S-curve centered at midpoint
        var sigmoid = 100.0 / (1.0 + Math.Exp(-SigmoidK * (rawScore - SigmoidMidpoint)));

        // Hybrid component: linear scaling from floor to 1.0
        var hybrid = rawScore < HybridFloor
            ? 0.0
            : (rawScore - HybridFloor) / (1.0 - HybridFloor) * 100.0;

        // Weighted combination
        var confidence = sigmoid * SigmoidWeight + hybrid * (1.0 - SigmoidWeight);

        return (int)Math.Clamp(Math.Round(confidence), 1, 100);
    }
}
