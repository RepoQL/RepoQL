using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provide an exclusive, cross-process lock to ensure a single host owns a repository at startup.
/// Complexity: Encapsulates filesystem lock acquisition and sharing-violation detection to keep host startup logic simple.
/// </summary>
/// <remarks>
/// See <see href="docs/flows/current/host-client-architecture.md">Host-Client Architecture</see> for the startup flow.
/// </remarks>
internal sealed class HostLock : IDisposable
{
    private readonly FileStream _stream;

    private HostLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static string GetLockPath(string repoRoot)
    {
        var repoqlDir = RepoLocator.EnsureRepoqlDirectory(repoRoot);
        return System.IO.Path.Combine(repoqlDir, "host.lock");
    }

    public static HostLock? TryAcquire(string repoRoot, out HostLockFailure failure, out Exception? error)
    {
        failure = HostLockFailure.None;
        error = null;
        var lockPath = GetLockPath(repoRoot);

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new HostLock(lockPath, stream);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            failure = HostLockFailure.Locked;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            failure = HostLockFailure.Unauthorized;
            error = ex;
            return null;
        }
        catch (Exception ex)
        {
            failure = HostLockFailure.Error;
            error = ex;
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private static bool IsSharingViolation(IOException ex)
    {
        const int ErrorSharingViolation = unchecked((int)0x80070020);
        const int ErrorLockViolation = unchecked((int)0x80070021);
        return ex.HResult is ErrorSharingViolation or ErrorLockViolation
            // On Unix, flock-based lock conflicts produce generic IOException;
            // permission errors are caught separately as UnauthorizedAccessException.
            || !OperatingSystem.IsWindows();
    }
}

/// <summary>
/// Purpose: Enumerate why a host lock acquisition failed.
/// Complexity: Keeps startup decisions explicit without leaking IO error details.
/// </summary>
internal enum HostLockFailure
{
    None,
    Locked,
    Unauthorized,
    Error
}
