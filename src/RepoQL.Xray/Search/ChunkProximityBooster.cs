namespace RepoQL.Xray.Search;

/// <summary>
/// Score for a document chunk (embedding region).
/// </summary>
public record ChunkScore(int StartLine, int EndLine, double Score);

/// <summary>
/// Boosts object scores based on proximity to high-scoring document chunks.
/// Objects that overlap or are adjacent to high-scoring chunks get boosted.
/// </summary>
public static class ChunkProximityBooster
{
    /// <summary>
    /// Maximum boost multiplier (30% increase).
    /// </summary>
    private const double MaxBoostFactor = 0.3;

    /// <summary>
    /// Lines of proximity to consider "adjacent" to a chunk.
    /// </summary>
    private const int AdjacentLineThreshold = 10;

    /// <summary>
    /// Apply chunk proximity boosts to object matches.
    /// Objects near high-scoring chunks get their RawScore boosted.
    /// </summary>
    public static void ApplyBoosts(
        IList<ObjectMatch> objects,
        IReadOnlyDictionary<string, IReadOnlyList<ChunkScore>> chunksByDocument)
    {
        foreach (var obj in objects)
        {
            if (!chunksByDocument.TryGetValue(obj.DocumentUri, out var chunks))
                continue;

            if (chunks.Count == 0)
                continue;

            var proximityScore = CalculateBestProximityScore(obj.LineStart, obj.LineEnd, chunks);

            if (proximityScore > 0)
            {
                // Boost: score * (1 + MaxBoostFactor * proximityScore)
                obj.RawScore *= (1.0 + MaxBoostFactor * proximityScore);
            }
        }
    }

    /// <summary>
    /// Calculate the best proximity score for an object against all chunks.
    /// Returns 0.0-1.0 based on overlap with highest-scoring chunk.
    /// </summary>
    private static double CalculateBestProximityScore(int objStart, int objEnd, IReadOnlyList<ChunkScore> chunks)
    {
        var bestScore = 0.0;

        foreach (var chunk in chunks)
        {
            var proximity = CalculateProximity(objStart, objEnd, chunk.StartLine, chunk.EndLine);
            var weightedScore = proximity * chunk.Score;

            if (weightedScore > bestScore)
                bestScore = weightedScore;
        }

        return bestScore;
    }

    /// <summary>
    /// Calculate proximity between object and chunk (0.0-1.0).
    /// - Full overlap: 1.0
    /// - Partial overlap: proportion of overlap
    /// - Adjacent (within threshold): decaying score
    /// - Far away: 0.0
    /// </summary>
    private static double CalculateProximity(int objStart, int objEnd, int chunkStart, int chunkEnd)
    {
        // Calculate overlap
        var overlapStart = Math.Max(objStart, chunkStart);
        var overlapEnd = Math.Min(objEnd, chunkEnd);

        if (overlapStart <= overlapEnd)
        {
            // There is overlap
            var overlapLength = overlapEnd - overlapStart + 1;
            var objLength = objEnd - objStart + 1;

            // Return proportion of object that overlaps with chunk
            return Math.Min(1.0, (double)overlapLength / objLength);
        }

        // No overlap - check if adjacent
        int distance = (objEnd < chunkStart)
            ? chunkStart - objEnd
            : objStart - chunkEnd;

        if (distance <= AdjacentLineThreshold)
        {
            // Adjacent - decay linearly from 0.5 to 0
            return 0.5 * (1.0 - (double)distance / AdjacentLineThreshold);
        }

        // Too far away
        return 0.0;
    }
}
