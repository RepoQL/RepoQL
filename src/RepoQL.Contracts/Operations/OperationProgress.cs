namespace RepoQL.Contracts;

/// <summary>
/// Progress snapshot for an operation tracking indexing work.
/// </summary>
/// <param name="TotalFiles">Count of URIs in scope (after deduplication).</param>
/// <param name="IndexedCount">Files that reached Indexed status.</param>
/// <param name="EmbeddedCount">Files that reached Embedded or NotApplicable status.</param>
/// <param name="FailedCount">Files that failed indexing or embedding.</param>
/// <param name="ReadyPercent">Completion percentage: (EmbeddedCount + FailedCount) * 100 / TotalFiles, or 100 if TotalFiles is 0.</param>
public record OperationProgress(
    int TotalFiles,
    int IndexedCount,
    int EmbeddedCount,
    int FailedCount,
    int ReadyPercent)
{
    /// <summary>
    /// Creates a progress snapshot from counts, computing ReadyPercent.
    /// </summary>
    public static OperationProgress Create(int totalFiles, int indexedCount, int embeddedCount, int failedCount)
    {
        var readyPercent = totalFiles == 0 ? 100 : (embeddedCount + failedCount) * 100 / totalFiles;
        return new OperationProgress(totalFiles, indexedCount, embeddedCount, failedCount, readyPercent);
    }

    /// <summary>
    /// Empty progress for an operation with no files.
    /// </summary>
    public static OperationProgress Empty { get; } = new(0, 0, 0, 0, 100);
}
