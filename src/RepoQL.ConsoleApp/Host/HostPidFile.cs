using System.Globalization;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Persist the host PID to coordinate shutdown and takeover logic.
/// Complexity: Encapsulates PID file read/write/delete semantics with safe failure handling.
/// </summary>
internal sealed class HostPidFile
{
    private readonly string _path;

    public HostPidFile(string repoRoot)
    {
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repoRoot);
        _path = System.IO.Path.Combine(repoqlDir, "host.pid");
    }

    public string FilePath => _path;

    public bool TryRead(out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(_path))
                return false;

            var contents = File.ReadAllText(_path).Trim();
            return int.TryParse(contents, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
        }
        catch
        {
            pid = 0;
            return false;
        }
    }

    public bool TryWrite(int pid, out Exception? error)
    {
        error = null;
        try
        {
            File.WriteAllText(_path, pid.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    public bool TryDelete(out Exception? error)
    {
        error = null;
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}
