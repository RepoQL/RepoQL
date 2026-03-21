namespace RepoQL.Explore.Search;

/// <summary>
/// JIT enrichment service. Computes ONNX embeddings for uncertain object candidates
/// and blends semantic scores into existing rankings from _explore_candidates.
///
/// Purpose: Refine explore results where semantic evidence is uncertain (inherited or missing).
/// Complexity: Query embedding, provenance-based uncertainty selection, ONNX batch embedding,
///   persistent caching, score blending.
/// </summary>
public interface IJitObjectSearchService
{
    /// <summary>
    /// Enrich already-ranked candidates with JIT ONNX embeddings.
    /// Selects uncertain object candidates (inherited/missing semantic evidence),
    /// computes embeddings, blends semantic scores into existing SQL-computed scores.
    /// Returns the full candidate list with updated scores for enriched objects.
    /// </summary>
    Task<JitEnrichmentResult> EnrichAsync(
        string question,
        IReadOnlyList<ExploreCandidate> candidates,
        JitEmbeddingCache jitCache,
        ObjectSearchConfig config,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result from JIT enrichment. Contains the full candidate list (enriched objects have
/// updated Score, SemScore, and SemProvenance fields).
/// </summary>
public record JitEnrichmentResult(
    IReadOnlyList<ExploreCandidate> Candidates,
    bool ScoresChanged);
