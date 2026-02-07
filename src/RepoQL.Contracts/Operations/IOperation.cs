namespace RepoQL.Contracts;

/// <summary>
/// A trackable, awaitable batch of indexing work.
///
/// Purpose: Track a set of URIs through indexing and embedding to completion,
/// providing progress visibility and failure surfacing.
///
/// Complexity: Operations poll UriRegistry every 500ms to detect state transitions.
/// The Completion task resolves when all URIs reach terminal state (embedded, not applicable, or failed).
/// Operations are agnostic to what triggered them (import, startup, reindex).
/// </summary>
public interface IOperation
{
    /// <summary>Unique identifier for this operation.</summary>
    string Id { get; }

    /// <summary>Human-readable description (convention: "kind: detail").</summary>
    string Description { get; }

    /// <summary>When the operation was created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>When the operation completed (null if still running).</summary>
    DateTimeOffset? CompletedAt { get; }

    /// <summary>Current state of the operation.</summary>
    OperationState State { get; }

    /// <summary>Current progress snapshot.</summary>
    OperationProgress Progress { get; }

    /// <summary>Append-only log of state transitions.</summary>
    IReadOnlyList<OperationEntry> Log { get; }

    /// <summary>
    /// Task that resolves when all URIs reach terminal state.
    /// Returns final progress on completion.
    /// Throws OperationCanceledException if cancelled.
    /// </summary>
    Task<OperationProgress> Completion { get; }

    /// <summary>
    /// Stops tracking. Already-indexed files remain.
    /// No-op if already in terminal state.
    /// </summary>
    void Cancel();

    /// <summary>
    /// Records a lifecycle milestone (scan_complete, hot_path_complete, ready, etc.).
    /// Thread-safe. No-op if operation is not in Running state.
    /// </summary>
    /// <param name="name">Milestone name (e.g., "scan_complete", "hot_path_complete").</param>
    /// <param name="detail">Optional detail (e.g., "1481 files, 1.2s").</param>
    void RecordMilestone(string name, string? detail = null);
}
