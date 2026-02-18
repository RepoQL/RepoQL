using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Helpers;

/// <summary>
/// Purpose: Provide runtime environment values used by commands and tools.
/// Complexity: Resolves active repository root from the current provider state and falls back to repo discovery.
/// </summary>
internal sealed class EnvironmentContext(RepoQlClientProvider clientProvider)
{
    public string RepoRootPath
    {
        get
        {
            var configured = clientProvider.GetConfiguredRepositoryPath();
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);

            return RepoLocator.FindRepoRoot();
        }
    }

    public string WorkingDirectoryPath => Path.GetFullPath(Directory.GetCurrentDirectory());
}
