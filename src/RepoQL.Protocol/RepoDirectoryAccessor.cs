using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Protocol;

/// <summary>
/// Wraps repository-specific filesystem operations (currently the .repoql directory) so they
/// can be accessed through <see cref="IFileProvider"/> abstractions.
/// </summary>
internal sealed class RepoDirectoryAccessor : IDisposable
{
    private readonly PhysicalFileProvider _repoqlProvider;

    public RepoDirectoryAccessor(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repository root cannot be null or empty.", nameof(repoRoot));

        RepoRoot = Path.GetFullPath(repoRoot);
        RepoqlDirectory = RepoLocator.EnsureRepoqlDirectory(RepoRoot);
        _repoqlProvider = new PhysicalFileProvider(RepoqlDirectory);
    }

    public string RepoRoot { get; }

    public string RepoqlDirectory { get; }

    /// <summary>
    /// Resolve the socket path used for RepoQL communication. Prefer the mapping file
    /// (.repoql/socket.path) when present; otherwise fall back to .repoql/repoql.sock.
    /// </summary>
    public string ResolveSocketPath()
    {
        var socketFile = _repoqlProvider.GetFileInfo("repoql.sock");
        var defaultSocket = socketFile.PhysicalPath ?? Path.Combine(RepoqlDirectory, "repoql.sock");
        var mapFile = _repoqlProvider.GetFileInfo("socket.path");

        if (!mapFile.Exists)
            return defaultSocket;

        var mapped = ReadAllText(mapFile);
        return string.IsNullOrWhiteSpace(mapped) ? defaultSocket : mapped.Trim();
    }

    private static string ReadAllText(IFileInfo file)
    {
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _repoqlProvider.Dispose();
    }
}
