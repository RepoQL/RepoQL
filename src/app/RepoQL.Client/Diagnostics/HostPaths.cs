using RepoQL.Contracts;

namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Path helpers for host state files used by client-side diagnostics.
/// </summary>
public static class HostPaths
{
    internal const string HostStderrFileName = "host.stderr.log";
    internal const string HostVersionFileName = "host.version";

    public static string GetHostStderrPath(string repoRoot)
        => Path.Combine(RepoLocator.EnsureRepoqlDirectory(repoRoot), HostStderrFileName);

    public static string GetHostVersionPath(string repoRoot)
        => Path.Combine(RepoLocator.EnsureRepoqlDirectory(repoRoot), HostVersionFileName);
}
