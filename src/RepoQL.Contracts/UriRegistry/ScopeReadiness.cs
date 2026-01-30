namespace RepoQL.Contracts;

/// <summary>
/// Result of checking whether a scope is ready for semantic search.
///
/// Purpose: Enable callers to verify that all files in a query scope are
/// indexed and embedded before executing semantic searches, ensuring
/// complete and reliable results.
///
/// Complexity: Simple immutable record with computed readiness flag.
/// </summary>
/// <param name="TotalFiles">Total number of files matching the scope pattern.</param>
/// <param name="IndexedCount">Number of files that are fully indexed.</param>
/// <param name="EmbeddedCount">Number of files that have embeddings ready.</param>
/// <param name="PendingIndex">Files that are not yet indexed.</param>
/// <param name="PendingEmbedding">Files that are indexed but not yet embedded.</param>
/// <param name="FailedFiles">Files that failed indexing or embedding.</param>
public record ScopeReadiness(
    int TotalFiles,
    int IndexedCount,
    int EmbeddedCount,
    IReadOnlyList<RepoUri> PendingIndex,
    IReadOnlyList<RepoUri> PendingEmbedding,
    IReadOnlyList<RepoUri> FailedFiles)
{
    /// <summary>
    /// Returns true if all files in scope are ready for semantic search.
    /// </summary>
    public bool IsReady => PendingIndex.Count == 0 && PendingEmbedding.Count == 0 && FailedFiles.Count == 0;

    /// <summary>
    /// Returns true if all files in scope are at least indexed (may not have embeddings).
    /// </summary>
    public bool IsIndexed => PendingIndex.Count == 0;

    /// <summary>
    /// Percentage of files that are fully ready (0-100).
    /// </summary>
    public int ReadyPercent => TotalFiles == 0 ? 100 : (EmbeddedCount * 100) / TotalFiles;

    /// <summary>
    /// Percentage of files that are indexed (0-100).
    /// </summary>
    public int IndexedPercent => TotalFiles == 0 ? 100 : (IndexedCount * 100) / TotalFiles;

    /// <summary>
    /// Human-readable summary of readiness state.
    /// </summary>
    public string Summary
    {
        get
        {
            if (IsReady)
                return $"Ready: {TotalFiles} files indexed and embedded";

            var parts = new List<string>();
            if (PendingIndex.Count > 0)
                parts.Add($"{PendingIndex.Count} pending index");
            if (PendingEmbedding.Count > 0)
                parts.Add($"{PendingEmbedding.Count} pending embedding");
            if (FailedFiles.Count > 0)
                parts.Add($"{FailedFiles.Count} failed");

            return $"Not ready: {string.Join(", ", parts)} (of {TotalFiles} files)";
        }
    }

    /// <summary>
    /// Empty readiness result for when no files match the scope.
    /// </summary>
    public static ScopeReadiness Empty => new(
        TotalFiles: 0,
        IndexedCount: 0,
        EmbeddedCount: 0,
        PendingIndex: [],
        PendingEmbedding: [],
        FailedFiles: []);
}
