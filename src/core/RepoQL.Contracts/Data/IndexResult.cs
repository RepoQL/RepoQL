namespace RepoQL.Contracts.Data;

/// <summary>
/// Result of successfully indexing an artifact.
/// </summary>
/// <param name="DocumentId">The document node ID.</param>
/// <param name="WasUpdate">True if this was an update to an existing document; false if new.</param>
public readonly record struct IndexResult(Guid DocumentId, bool WasUpdate);
