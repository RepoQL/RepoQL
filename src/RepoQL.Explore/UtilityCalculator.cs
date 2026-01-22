namespace RepoQL.Explore;

/// <summary>
/// Calculates utility scores for value-based token allocation.
///
/// Implements the utility model formula:
/// U(item, option) = P_relevance × V(option, intent) × evidenceQuality × novelty
///
/// Where:
/// - P_relevance: Normalized confidence (0-1) from search results
/// - V(option, intent): Value matrix (provided by OptionValue)
/// - evidenceQuality: 1.0 for semantic hit, 0.7 for lexical-only
/// - novelty: Diminishing returns for same type/file (1.0, 0.83, 0.71...)
/// </summary>
public static class UtilityCalculator
{
    /// <summary>
    /// Evidence quality weight for semantic search hits (best evidence).
    /// </summary>
    public const double SemanticEvidenceQuality = 1.0;

    /// <summary>
    /// Evidence quality weight for lexical-only hits (name or regex match without semantic confirmation).
    /// </summary>
    public const double LexicalOnlyEvidenceQuality = 0.7;

    /// <summary>
    /// Converts a confidence score (0-100) from search results to a normalized relevance value (0-1).
    /// </summary>
    /// <param name="confidence">Confidence score from search results, typically 0-100.</param>
    /// <returns>Normalized relevance value in range [0, 1].</returns>
    /// <remarks>
    /// This maps search confidence scores to the P_relevance component of the utility formula.
    /// For example, a confidence of 75 becomes a relevance of 0.75.
    /// </remarks>
    public static double CalculateRelevance(int confidence)
    {
        // Clamp confidence to [0, 100] range and normalize
        var clamped = Math.Max(0, Math.Min(100, confidence));
        return clamped / 100.0;
    }

    /// <summary>
    /// Determines evidence quality based on the types of search hits detected.
    /// </summary>
    /// <param name="hasSemanticScore">Whether the result has a semantic embedding score (semantic search hit).</param>
    /// <param name="hasNameHit">Whether the result matched a symbol name (exact or partial).</param>
    /// <param name="hasRegexHit">Whether the result matched a regex pattern.</param>
    /// <returns>Evidence quality score: 1.0 for semantic hits, 0.7 for lexical-only hits, or 0 if no evidence.</returns>
    /// <remarks>
    /// Evidence quality prioritizes semantic matches over lexical matches:
    /// - If hasSemanticScore is true, returns 1.0 (strongest evidence)
    /// - If only lexical matches exist (name or regex), returns 0.7 (weaker evidence)
    /// - If no evidence is detected, returns 0.0
    ///
    /// This reflects the design principle that semantic embeddings provide more meaningful
    /// relevance signals than simple text matching.
    /// </remarks>
    public static double CalculateEvidenceQuality(bool hasSemanticScore, bool hasNameHit, bool hasRegexHit)
    {
        // Semantic evidence is strongest
        if (hasSemanticScore)
            return SemanticEvidenceQuality;

        // Lexical evidence (name or regex) is weaker
        if (hasNameHit || hasRegexHit)
            return LexicalOnlyEvidenceQuality;

        // No evidence detected
        return 0.0;
    }
}
