namespace RepoQL.Protocol;

/// <summary>
/// Result of an import operation including progress information.
/// </summary>
/// <param name="Status">Pipeline status after import.</param>
/// <param name="TotalFiles">Total files in the import scope.</param>
/// <param name="IndexedCount">Files that reached indexed state.</param>
/// <param name="EmbeddedCount">Files that have structure embeddings.</param>
/// <param name="FailedCount">Files that failed indexing or embedding.</param>
public record ImportResult(
    Contracts.PipelineStatus Status,
    int TotalFiles,
    int IndexedCount,
    int EmbeddedCount,
    int FailedCount)
{
    /// <summary>True if any files failed during import.</summary>
    public bool HasFailures => FailedCount > 0;

    /// <summary>True if operation tracking was used (TotalFiles > 0).</summary>
    public bool HasOperationProgress => TotalFiles > 0;
}
