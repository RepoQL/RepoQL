using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Trust signal describing indexing/semantic readiness and execution timing.
/// </summary>
/// <param name="IndexTotal">Total number of discovered files.</param>
/// <param name="IndexPending">Number of files pending indexing.</param>
/// <param name="IndexFailed">Number of files that failed indexing.</param>
/// <param name="IndexStale">Number of files marked stale since indexing.</param>
/// <param name="SemanticEnabled">True if semantic embeddings are enabled.</param>
/// <param name="SemanticReady">True when semantic indexing is complete for all applicable files.</param>
/// <param name="SemanticPercent">Percent of embedding-applicable files in a final semantic state. Failures count as final and are reported separately.</param>
/// <param name="ExecutionTimeMs">Query execution time in milliseconds.</param>
public record TrustSignal(
    int IndexTotal,
    int IndexPending,
    int IndexFailed,
    int IndexStale,
    bool SemanticEnabled,
    bool SemanticReady,
    int SemanticPercent,
    long ExecutionTimeMs
)
{
    /// <summary>
    /// Absolute search quality tier derived from top raw score ("strong", "moderate", "weak", "exhaustive").
    /// Null when no quality tier is available.
    /// </summary>
    public string? SearchQualityTier { get; init; }

    /// <summary>
    /// Number of documents scored above the coverage threshold.
    /// Null when coverage should not be shown.
    /// </summary>
    public int? CoverageAboveThreshold { get; init; }

    /// <summary>
    /// Total number of documents considered for coverage.
    /// Null when coverage should not be shown.
    /// </summary>
    public int? CoverageTotalDocuments { get; init; }

    /// <summary>
    /// True when every document in scope scored above threshold.
    /// </summary>
    public bool CoverageAllInScope { get; init; }

    /// <summary>
    /// Create trust signal from cached URI registry summary.
    /// </summary>
    public static TrustSignal FromSummary(RegistrySummary summary, long executionTimeMs, bool semanticEnabled)
    {
        var semanticPercent = semanticEnabled ? summary.SemanticPercent : 0;
        var semanticReady = semanticEnabled && semanticPercent == 100;

        return new TrustSignal(
            IndexTotal: summary.TotalFiles,
            IndexPending: summary.IndexPending,
            IndexFailed: summary.IndexFailed,
            IndexStale: summary.IndexStale,
            SemanticEnabled: semanticEnabled,
            SemanticReady: semanticReady,
            SemanticPercent: semanticPercent,
            ExecutionTimeMs: executionTimeMs);
    }

    /// <summary>
    /// Create trust signal from diagnostics snapshot (fallback path when summary is unavailable).
    /// </summary>
    public static TrustSignal FromDiagnostics(
        int hotPathDepth,
        int idlePending,
        int analysisDepth,
        int writerPending,
        long executionTimeMs,
        bool embedEnabled)
    {
        var indexPending = hotPathDepth + idlePending + analysisDepth + writerPending;
        var semanticReady = embedEnabled && indexPending == 0;
        var semanticPercent = semanticReady ? 100 : 0;

        return new TrustSignal(
            IndexTotal: 0,
            IndexPending: indexPending,
            IndexFailed: 0,
            IndexStale: 0,
            SemanticEnabled: embedEnabled,
            SemanticReady: semanticReady,
            SemanticPercent: semanticPercent,
            ExecutionTimeMs: executionTimeMs);
    }
}
