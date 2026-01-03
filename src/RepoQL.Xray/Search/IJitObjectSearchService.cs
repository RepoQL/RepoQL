namespace RepoQL.Xray.Search;

/// <summary>
/// Interface for JIT (Just-In-Time) object search service.
/// Uses local ONNX to compute both query and object embeddings at search time,
/// ensuring self-consistent similarity comparisons.
/// </summary>
public interface IJitObjectSearchService
{
    /// <summary>
    /// Execute JIT object search with softmax document selection and JIT embeddings.
    /// </summary>
    /// <param name="question">Search query.</param>
    /// <param name="scope">Scope filter (glob pattern).</param>
    /// <param name="boostPattern">Regex patterns to boost matches (comma-separated).</param>
    /// <param name="penalizePattern">Regex patterns to de-rank matches (comma-separated).</param>
    /// <param name="config">Search configuration.</param>
    /// <param name="jitCache">JIT embedding cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<JitObjectSearchResult> SearchAsync(
        string? question,
        string? scope,
        string? boostPattern,
        string? penalizePattern,
        ObjectSearchConfig config,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result from JIT object search.
/// </summary>
public record JitObjectSearchResult(
    IReadOnlyList<DocumentExpansionCandidate> SelectedDocuments,
    IReadOnlyList<ObjectCandidate> ScoredObjects,
    NormalizedQuerySignals QuerySignals
);
