namespace RepoQL.Protocol;

/// <summary>
/// Result of an import operation including progress information.
/// </summary>
/// <param name="Status">Pipeline status after import.</param>
/// <param name="TotalFiles">Total files in the import scope.</param>
/// <param name="IndexedCount">Files that reached indexed state.</param>
/// <param name="EmbeddedCount">Files that have structure embeddings.</param>
/// <param name="FailedCount">Files that failed indexing or embedding.</param>
/// <param name="Message">Human-readable import summary from the host.</param>
/// <param name="OperationId">Operation identifier when import continues asynchronously.</param>
public record ImportResult(
    Contracts.PipelineStatus Status,
    int TotalFiles,
    int IndexedCount,
    int EmbeddedCount,
    int FailedCount,
    string? Message = null,
    string? OperationId = null)
{
    /// <summary>True if any files failed during import.</summary>
    public bool HasFailures => FailedCount > 0;

    /// <summary>True when the host returned operation tracking information.</summary>
    public bool HasOperationProgress => !string.IsNullOrWhiteSpace(OperationId) || TotalFiles > 0;
}
