namespace RepoQL.Rendering;

/// <summary>
/// Status of the indexer and query timing for context about data completeness.
/// </summary>
/// <param name="IndexPending">Number of files pending indexing, 0 if ready.</param>
/// <param name="SemanticReady">True if semantic index is ready (embeddings enabled and indexing complete).</param>
/// <param name="SemanticEnabled">True if semantic embeddings are enabled.</param>
/// <param name="ElapsedMs">Query execution time in milliseconds.</param>
public record IndexerStatus(
    int IndexPending,
    bool SemanticReady,
    bool SemanticEnabled,
    long ElapsedMs
)
{
    /// <summary>
    /// Create status from diagnostics snapshot.
    /// </summary>
    public static IndexerStatus FromDiagnostics(
        int hotPathDepth,
        int idlePending,
        int analysisDepth,
        int writerPending,
        long elapsedMs,
        bool embedEnabled)
    {
        var indexPending = hotPathDepth + idlePending + analysisDepth + writerPending;
        var semanticReady = embedEnabled && indexPending == 0;
        return new IndexerStatus(indexPending, semanticReady, embedEnabled, elapsedMs);
    }
}
