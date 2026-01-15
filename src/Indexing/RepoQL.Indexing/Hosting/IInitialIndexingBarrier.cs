namespace RepoQL.Indexing.Hosting;

/// <summary>
/// Barrier that signals when initial file indexing has completed.
/// Used by services that need to wait for the index to be ready before starting.
/// </summary>
public interface IInitialIndexingBarrier
{
    /// <summary>
    /// Task that completes when initial file indexing is done.
    /// </summary>
    Task InitialScanCompleted { get; }
}
