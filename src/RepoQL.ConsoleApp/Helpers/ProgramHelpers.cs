using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Helpers;

internal static class ProgramHelpers
{
    public static string ResolveRepo(string? repo)
        => RepoLocator.FindRepoRoot(string.IsNullOrWhiteSpace(repo) ? Directory.GetCurrentDirectory() : repo);

    public static async Task<RepoQlClient> CreateClientAsync(string repo, TimeSpan? defaultTimeout = null, CancellationToken cancellationToken = default)
        => await RepoQlClient.CreateAsync(new RepoQlClientOptions
        {
            RepositoryPath = repo,
            SocketPath = ResolveSocketPath(repo),
            DefaultTimeout = defaultTimeout
        }, cancellationToken);

    // Helper used by tests and local commands to build the indexing DI container
    public static ServiceProvider BuildCoreProvider(string repoRoot)
    {
        var services = new ServiceCollection();
        services.AddRepoIndexer(repoRoot);
        return services.BuildServiceProvider();
    }
    
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