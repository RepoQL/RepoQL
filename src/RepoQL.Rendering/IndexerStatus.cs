namespace RepoQL.Rendering;

/// <summary>
/// Status of the indexer for context about data completeness.
/// </summary>
/// <param name="Stage">Current indexer stage (e.g., "Discovery", "Indexing", "SemanticIndexing", "Complete").</param>
/// <param name="Progress">Progress percentage 0-100, null if unknown.</param>
/// <param name="PendingFiles">Number of files pending indexing, null if unknown.</param>
public record IndexerStatus(
    string Stage,
    int? Progress = null,
    int? PendingFiles = null
)
{
    /// <summary>
    /// Indexer has completed all stages.
    /// </summary>
    public static IndexerStatus Complete { get; } = new("Complete", 100, 0);

    /// <summary>
    /// Indexer status is unknown.
    /// </summary>
    public static IndexerStatus Unknown { get; } = new("Unknown");
}
