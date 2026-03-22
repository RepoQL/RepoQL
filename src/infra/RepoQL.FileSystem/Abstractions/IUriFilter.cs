using RepoQL.Contracts;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
/// Predicate to include/exclude URIs and directories. Scheme-aware implementations are expected.
/// </summary>
public interface IUriFilter
{
    /// <summary>Return true if the resource URI should be indexed.</summary>
    bool IncludeFile(RepoUri uri);
}