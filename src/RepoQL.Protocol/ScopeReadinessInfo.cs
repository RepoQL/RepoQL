namespace RepoQL.Protocol;

/// <summary>
/// Lightweight scope readiness info parsed from UDF results.
///
/// Purpose: Provides a typed representation of scope readiness status
/// that can be used by tools to check if a scope is ready for semantic search.
///
/// Complexity: Simple immutable record with factory for "ready" state.
/// </summary>
/// <param name="IsReady">True if all files in scope are indexed and have structure embeddings.</param>
/// <param name="TotalFiles">Total number of files matching the scope pattern.</param>
/// <param name="IndexedCount">Number of files that are fully indexed.</param>
/// <param name="EmbeddedCount">Number of files that have structure embeddings ready.</param>
/// <param name="PendingIndex">Number of files pending indexing.</param>
/// <param name="PendingEmbedding">Number of files pending embedding.</param>
/// <param name="FailedCount">Number of files that failed indexing or embedding.</param>
/// <param name="ReadyPercent">Percentage of files that are fully ready (0-100).</param>
/// <param name="Summary">Human-readable summary of readiness state.</param>
public record ScopeReadinessInfo(
    bool IsReady,
    int TotalFiles,
    int IndexedCount,
    int EmbeddedCount,
    int PendingIndex,
    int PendingEmbedding,
    int FailedCount,
    int ReadyPercent,
    string Summary)
{
    /// <summary>
    /// A ready state for when no files match the scope or scope is empty.
    /// </summary>
    public static ScopeReadinessInfo Ready { get; } = new(
        IsReady: true,
        TotalFiles: 0,
        IndexedCount: 0,
        EmbeddedCount: 0,
        PendingIndex: 0,
        PendingEmbedding: 0,
        FailedCount: 0,
        ReadyPercent: 100,
        Summary: "Ready");
}
