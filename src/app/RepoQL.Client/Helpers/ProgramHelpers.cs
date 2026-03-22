using RepoQL.Contracts;

namespace RepoQL.Client.Helpers;

public static class ProgramHelpers
{
    public static string ResolveRepo(string? repo)
        => RepoLocator.FindRepoRoot(string.IsNullOrWhiteSpace(repo) ? Directory.GetCurrentDirectory() : repo);

}
