using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem;

/// <summary>
/// Trivial URI filter that allows every file. Useful for tests.
/// </summary>
public sealed class NoOpUriFilter : IUriFilter
{
    public bool IncludeFile(RepoUri uri) => true;
}