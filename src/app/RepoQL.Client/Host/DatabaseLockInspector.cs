using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RepoQL.ConsoleApp.Diagnostics;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Identify processes holding a lock on the DuckDB file.
/// Complexity: Uses OS-specific process enumeration strategies with safe fallbacks.
/// </summary>
internal static class DatabaseLockInspector
{
    public static ProcessInfo? TryGetLockHolder(string databasePath, Serilog.ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetLockHolderWindows(databasePath, logger);

            if (OperatingSystem.IsLinux())
                return TryGetLockHolderLinux(databasePath, logger);

            if (OperatingSystem.IsMacOS())
                return TryGetLockHolderMac(databasePath, logger);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Failed to inspect database lock holder.");
        }

        return null;
    }

    private static ProcessInfo? TryGetLockHolderLinux(string databasePath, Serilog.ILogger logger)
    {
        var normalizedPath = Path.GetFullPath(databasePath);
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
                continue;

            var fdDir = Path.Combine(dir, "fd");
            if (!Directory.Exists(fdDir))
                continue;

            try
            {
                foreach (var fd in Directory.EnumerateFiles(fdDir))
                {
                    var linkTarget = new FileInfo(fd).LinkTarget;
                    if (string.IsNullOrWhiteSpace(linkTarget))
                        continue;

                    var cleaned = linkTarget.Replace(" (deleted)", string.Empty, StringComparison.Ordinal);
                    if (string.Equals(cleaned, normalizedPath, StringComparison.Ordinal))
                    {
                        var name = TryGetProcessName(pid);
                        return new ProcessInfo(pid, name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to inspect /proc/{Pid}/fd for lock holder.", pid);
            }
        }

        return null;
    }

    private static ProcessInfo? TryGetLockHolderMac(string databasePath, Serilog.ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo("lsof", $"-n -F pc -- \"{databasePath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            if (string.IsNullOrWhiteSpace(output))
                return null;

            int? pid = null;
            string? name = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('p'))
                {
                    if (int.TryParse(line[1..], out var parsed))
                        pid = parsed;
                }
                else if (line.StartsWith('c'))
                {
                    name = line[1..];
                }

                if (pid.HasValue && !string.IsNullOrWhiteSpace(name))
                    break;
            }

            return pid.HasValue ? new ProcessInfo(pid.Value, name) : null;
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Failed to run lsof for lock holder detection.");
            return null;
        }
    }

    private static ProcessInfo? TryGetLockHolderWindows(string databasePath, Serilog.ILogger logger)
    {
        var sessionKey = Guid.NewGuid().ToString("N");
        var result = RmStartSession(out var sessionHandle, 0, sessionKey);
        if (result != 0)
        {
            logger.Debug("Restart Manager session start failed with code {Code}.", result);
            return null;
        }

        try
        {
            var resources = new[] { databasePath };
            result = RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
            if (result != 0)
            {
                logger.Debug("Restart Manager resource registration failed with code {Code}.", result);
                return null;
            }

            uint needed = 0;
            uint count = 0;
            uint rebootReasons = 0;
            result = RmGetList(sessionHandle, out needed, ref count, null, ref rebootReasons);
            if (result == ErrorMoreData)
            {
                var processInfo = new RM_PROCESS_INFO[needed];
                count = needed;
                result = RmGetList(sessionHandle, out needed, ref count, processInfo, ref rebootReasons);
                if (result == 0 && count > 0)
                {
                    var info = processInfo[0];
                    return new ProcessInfo(info.Process.dwProcessId, info.strAppName);
                }
            }

            return null;
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private const int ErrorMoreData = 234;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }
}
