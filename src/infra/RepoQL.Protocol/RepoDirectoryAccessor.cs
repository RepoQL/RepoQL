using RepoQL.Contracts;

namespace RepoQL.Protocol;

/// <summary>
/// Wraps repository-specific filesystem operations (currently the .repoql directory).
/// </summary>
internal sealed class RepoDirectoryAccessor : IDisposable
{
    public RepoDirectoryAccessor(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repository root cannot be null or empty.", nameof(repoRoot));

        RepoRoot = Path.GetFullPath(repoRoot);
        RepoqlDirectory = RepoLocator.EnsureRepoqlDirectory(RepoRoot);
    }

    public string RepoRoot { get; }

    public string RepoqlDirectory { get; }

    /// <summary>
    /// Resolve the socket path used for RepoQL communication. Prefer the mapping file
    /// (.repoql/socket.path) when present; otherwise fall back to .repoql/repoql.sock.
    /// </summary>
    public string ResolveSocketPath()
        => RepoqlSocketPathResolver.ResolvePhysical(RepoRoot);

    public void Dispose()
    {
    }
}
