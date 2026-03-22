namespace RepoQL.Contracts;

/// <summary>
/// Terminal state of an operation tracking indexing work.
/// </summary>
public enum OperationState
{
    /// <summary>Operation is actively polling for file status changes.</summary>
    Running,

    /// <summary>All files reached terminal state with no failures.</summary>
    Completed,

    /// <summary>All files reached terminal state but some failed.</summary>
    CompletedWithFailures,

    /// <summary>Operation was cancelled before completion.</summary>
    Cancelled
}
