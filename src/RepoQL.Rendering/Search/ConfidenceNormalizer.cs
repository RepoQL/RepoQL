namespace RepoQL.Rendering.Search;

/// <summary>
/// Normalizes raw scores to 1-100 confidence values against a fixed scale.
/// </summary>
public static class ConfidenceNormalizer
{
    /// <summary>
    /// Score threshold representing 100% confidence.
    /// Calibrated for mixed file_search() + search() output:
    /// - file_search() combines BM25 + semantic, produces 1.0-1.5 for strong matches
    /// - search() objects produce 0.5-1.0 for strong matches
    /// </summary>
    public const double MaxConfidenceScore = 1.5;

    /// <summary>
    /// Score below which confidence is effectively 1%.
    /// </summary>
    public const double MinConfidenceScore = 0.0;

    /// <summary>
    /// Normalize raw scores to 1-100 confidence range against fixed thresholds.
    /// </summary>
    public static IReadOnlyList<SearchResult> Normalize(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0)
            return results;

        var normalized = new SearchResult[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var confidence = ScoreToConfidence(result.RawScore);
            normalized[i] = result with { Confidence = confidence };
        }

        return normalized;
    }

    /// <summary>
    /// Normalize a mutable list in place.
    /// </summary>
    public static void NormalizeInPlace(IList<SearchResult> results)
    {
        if (results.Count == 0) return;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var confidence = ScoreToConfidence(result.RawScore);
            results[i] = result with { Confidence = confidence };
        }
    }

    /// <summary>
    /// Convert a raw score to 1-100 confidence.
    /// </summary>
    public static int ScoreToConfidence(double rawScore)
    {
        // Clamp to [0, MaxConfidenceScore]
        var clamped = Math.Clamp(rawScore, MinConfidenceScore, MaxConfidenceScore);

        // Linear scale to 1-100
        var normalized = clamped / MaxConfidenceScore;
        return Math.Max(1, (int)Math.Round(normalized * 99) + 1);
    }
}
