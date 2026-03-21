using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Helpers;

internal static class ProgramHelpers
{
    public static string ResolveRepo(string? repo)
        => RepoLocator.FindRepoRoot(string.IsNullOrWhiteSpace(repo) ? Directory.GetCurrentDirectory() : repo);

}
