namespace RepoQL.Contracts;

/// <summary>
/// Configuration for the repository being indexed.
/// Injected into components that need to know the repository root path.
/// </summary>
public record RepositoryConfiguration
{
    /// <summary>
    /// The absolute path to the repository root.
    /// </summary>
    public required string Path { get; init; }
}
