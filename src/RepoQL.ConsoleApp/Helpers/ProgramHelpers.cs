using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Helpers;

internal static class ProgramHelpers
{
    public static string ResolveRepo(string? repo)
        => RepoLocator.FindRepoRoot(string.IsNullOrWhiteSpace(repo) ? Directory.GetCurrentDirectory() : repo);

    public static string ResolveSocketPath(string repoPath)
    {
        var env = Environment.GetEnvironmentVariable("REPOQL_SOCKET");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var mapFile = Path.Combine(repoPath, ".repoql", "socket.path");
        if (!File.Exists(mapFile)) 
            return Path.Combine(repoPath, ".repoql", "repoql.sock");
        try
        {
            var p = File.ReadAllText(mapFile).Trim();
            if (!string.IsNullOrWhiteSpace(p))
                return p;
        }
        catch
        {
            // Suppress
        }
        return Path.Combine(repoPath, ".repoql", "repoql.sock");
    }
}