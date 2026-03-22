namespace RepoQL.Contracts;

/// <summary>
/// Creates and tracks operations. Singleton, registered in DI.
///
/// Purpose: Provide a central registry of operations for observability
/// and enable callers to track batches of indexing work.
///
/// Complexity: Operations are retained in memory until host restart.
/// All URIs in scope must be registered in UriRegistry before creating an operation.
/// </summary>
public interface IOperationManager
{
    /// <summary>
    /// Creates a new operation tracking the given URIs.
    /// Scope is deduplicated by URI. All URIs must already be in UriRegistry.
    /// </summary>
    /// <param name="description">Human-readable description (convention: "kind: detail").</param>
    /// <param name="scope">URIs to track (deduplicated and immutable after creation).</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <returns>The created operation.</returns>
    IOperation CreateOperation(
        string description,
        IEnumerable<RepoUri> scope,
        IProgress<OperationProgress>? progress = null);

    /// <summary>Gets operation by ID, or null if not found.</summary>
    IOperation? GetOperation(string id);

    /// <summary>All operations (active and completed, until restart).</summary>
    IReadOnlyList<IOperation> Operations { get; }

    /// <summary>Only operations not yet in terminal state.</summary>
    IReadOnlyList<IOperation> ActiveOperations { get; }
}
