namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Centralize repository-local ".repoql" path conventions and constants.
/// Complexity: Simple string composition so callers share a single source of truth.
/// </summary>
public static class RepoqlPaths
{
    public const string RepoqlDirectoryName = ".repoql";
    public const string SocketFileName = "repoql.sock";
    public const string SocketMapFileName = "socket.path";

    public static string GetRepoqlDirectoryPath(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repository root cannot be null or empty.", nameof(repoRoot));

        return Path.Combine(Path.GetFullPath(repoRoot), RepoqlDirectoryName);
    }

    public static string GetSocketMappingPath(string repoRoot)
        => Path.Combine(GetRepoqlDirectoryPath(repoRoot), SocketMapFileName);

    public static string GetDefaultSocketPath(string repoRoot)
        => Path.Combine(GetRepoqlDirectoryPath(repoRoot), SocketFileName);
}
