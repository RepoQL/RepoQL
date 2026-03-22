using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RepoQL.Client.Host;

/// <summary>
/// Purpose: Terminate processes in a platform-aware, best-effort manner.
/// Complexity: Wraps OS-specific termination paths while enforcing timeouts.
/// </summary>
internal static class ProcessTermination
{
    public static async Task<bool> TryTerminateAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                process.Kill(entireProcessTree: true);
                return await WaitForExitAsync(process.Id, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }

            if (UnixSignal.TrySendTerm(process.Id))
            {
                if (await WaitForExitAsync(process.Id, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
                    return true;
            }

            if (UnixSignal.TrySendKill(process.Id))
            {
                return await WaitForExitAsync(process.Id, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            process.Dispose();
        }
    }

    public static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessRunning(pid))
            {
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Purpose: Send Unix signals for graceful or forceful termination.
    /// Complexity: Wraps libc kill calls with minimal surface for host takeover.
    /// </summary>
    private static class UnixSignal
    {
        private const int SigTerm = 15;
        private const int SigKill = 9;

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        public static bool TrySendTerm(int pid)
            => kill(pid, SigTerm) == 0;

        public static bool TrySendKill(int pid)
            => kill(pid, SigKill) == 0;
    }
}
