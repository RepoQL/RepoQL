using RepoQL.Contracts;

namespace RepoQL.Client.Helpers;

/// <summary>
/// Purpose: Provide runtime environment values used by commands and tools.
/// Complexity: Resolves active repository root from the current provider state and falls back to repo discovery.
/// </summary>
internal sealed class EnvironmentContext
{
    private readonly RepoQlClientProvider? _clientProvider;
    private readonly string? _fixedRepoRoot;

    public EnvironmentContext(RepoQlClientProvider clientProvider) => _clientProvider = clientProvider;

    /// <summary>Test-only constructor that bypasses client provider lookup.</summary>
    internal EnvironmentContext(string repoRootPath) => _fixedRepoRoot = repoRootPath;

    public string RepoRootPath
    {
        get
        {
            if (_fixedRepoRoot is not null)
                return _fixedRepoRoot;

            var configured = _clientProvider!.GetConfiguredRepositoryPath();
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);

            return RepoLocator.FindRepoRoot();
        }
    }

    public string WorkingDirectoryPath => Path.GetFullPath(Directory.GetCurrentDirectory());
}
