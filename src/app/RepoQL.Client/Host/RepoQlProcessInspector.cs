using System.Diagnostics;

namespace RepoQL.Client.Host;

/// <summary>
/// Purpose: Identify RepoQL processes safely before termination.
/// Complexity: Checks process name and executable path while handling restricted access.
/// </summary>
internal static class RepoQlProcessInspector
{
    public static bool TryGetRepoQlProcess(int pid, out Process process)
    {
        try
        {
            process = Process.GetProcessById(pid);
            if (IsRepoQlProcess(process))
                return true;

            process.Dispose();
        }
        catch (ArgumentException)
        {
            // ignored
        }
        catch (InvalidOperationException)
        {
            // ignored
        }

        process = null!;
        return false;
    }

    public static bool IsRepoQlProcess(Process process)
    {
        if (process.ProcessName.Contains("repoql", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path) &&
                path.Contains("repoql", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // Ignore restricted process info.
        }

        return false;
    }
}
