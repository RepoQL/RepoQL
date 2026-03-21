namespace RepoQL.Explore.Search;

/// <summary>
/// Converts pipeline scores to 0-100 confidence values.
/// Scores are already floor-normalized in the search_pipeline macro
/// (floor=0.33 subtracted, rescaled to [0,1]), so this is a direct mapping.
/// </summary>
public static class ConfidenceNormalizer
{
    /// <summary>
    /// Normalize raw scores to 1-100 confidence range.
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
    /// Score is already normalized to [0,1] by the pipeline macro.
    /// Just scale to percentage.
    /// </summary>
    public static int ScoreToConfidence(double rawScore)
    {
        return (int)Math.Clamp(Math.Round(rawScore * 100.0), 1, 100);
    }
}
