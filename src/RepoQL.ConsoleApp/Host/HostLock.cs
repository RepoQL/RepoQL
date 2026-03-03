using System.Text;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provide an exclusive, cross-process lock to ensure a single host owns a repository at startup.
/// Complexity: Encapsulates filesystem lock acquisition, PID embedding (for zombie detection), and sharing-violation detection.
/// The lock file doubles as the PID file — PID is written in "PID:nnn\n" format and readable by competing processes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design</b>: The lock file at <c>.repoql/host.lock</c> serves dual purpose — exclusive lock AND PID record.
/// This eliminates the race between acquiring a lock and writing a separate PID file, which previously allowed
/// concurrent processes to see a lock without knowing who held it.
/// </para>
/// <para>
/// <b>Sharing model</b>: The holder opens with <c>FileShare.Read</c>, so competing processes can read the PID
/// via <see cref="TryReadHolderPid"/> while the lock is held. Write exclusivity is enforced by the OS —
/// only one process can hold <c>FileAccess.ReadWrite</c> at a time.
/// </para>
/// <para>
/// <b>Acquisition is two-phase</b>: Phase 1 opens the FileStream (lock acquisition). Phase 2 writes the PID.
/// These are separate try blocks so that IO errors during PID write (e.g. ENOSPC) are reported as
/// <see cref="HostLockFailure.Error"/>, not <see cref="HostLockFailure.Locked"/>. If PID write fails,
/// the stream is disposed so the lock is released — no leaked handles.
/// </para>
/// <para>
/// <b>Lifecycle</b>: The lock is held for the lifetime of the <see cref="HostLock"/> instance. Disposing it
/// releases the FileStream, which releases the OS-level lock. The lock file itself is never deleted —
/// stale lock files (from crashes) are simply overwritten on next acquisition since the OS releases
/// file locks when a process exits.
/// </para>
/// <para>
/// See <see href="docs/flows/current/host-client-architecture.md">Host-Client Architecture</see> for the startup flow.
/// </para>
/// </remarks>
internal sealed class HostLock : IDisposable
{
    private const string PidPrefix = "PID:";
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

    /// <summary>
    /// Attempt to acquire the host lock for the given repository.
    /// Returns the lock on success, or null with <paramref name="failure"/> indicating why.
    /// </summary>
    /// <remarks>
    /// Two-phase acquisition: (1) open the file for exclusive write access, (2) write the PID.
    /// Phase 1 failures from sharing violations → <see cref="HostLockFailure.Locked"/>.
    /// Phase 2 failures (IO during PID write) → <see cref="HostLockFailure.Error"/> with stream disposed.
    /// This separation prevents Unix IO errors (ENOSPC, EIO) from being misclassified as lock contention.
    /// </remarks>
    public static HostLock? TryAcquire(string repoRoot, out HostLockFailure failure, out Exception? error)
    {
        failure = HostLockFailure.None;
        error = null;
        var lockPath = GetLockPath(repoRoot);

        // Phase 1: Acquire the OS-level file lock.
        // IsSharingViolation determines if the IOException means "another process holds this file."
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
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

        // Phase 2: Write our PID so zombie detection can identify us.
        // Separate try block: IO errors here are disk/OS problems, not lock contention.
        // On failure, dispose the stream to release the lock — don't leave leaked handles.
        try
        {
            stream.SetLength(0);
            var content = Encoding.ASCII.GetBytes($"{PidPrefix}{Environment.ProcessId}\n");
            stream.Write(content);
            stream.Flush();

            return new HostLock(lockPath, stream);
        }
        catch (Exception ex)
        {
            stream.Dispose();
            failure = HostLockFailure.Error;
            error = ex;
            return null;
        }
    }

    /// <summary>
    /// Read the PID of the process currently holding the lock file.
    /// Returns false if the file doesn't exist, can't be read, or contains invalid content.
    /// </summary>
    /// <remarks>
    /// Opens with <c>FileAccess.Read, FileShare.ReadWrite</c> so it can read while the holder has
    /// the file open for writing. Validates the <c>PID:</c> prefix before parsing to reject partial
    /// or corrupt writes. Any exception (file vanished between exists-check and open, permission
    /// denied, etc.) returns false — callers should treat unreadable PIDs as "unknown holder."
    /// </remarks>
    public static bool TryReadHolderPid(string repoRoot, out int pid)
    {
        pid = 0;
        try
        {
            var lockPath = GetLockPath(repoRoot);
            if (!File.Exists(lockPath))
                return false;

            // FileShare.ReadWrite allows reading even while the holder has it open for writing.
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[64];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                return false;

            var content = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
            if (!content.StartsWith(PidPrefix, StringComparison.Ordinal))
                return false;

            return int.TryParse(content.AsSpan(PidPrefix.Length), out pid) && pid > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    /// <summary>
    /// Detect whether an IOException represents a file-sharing conflict (another process holds the lock).
    /// </summary>
    /// <remarks>
    /// On Windows, flock conflicts surface as specific HResult codes (ERROR_SHARING_VIOLATION 0x80070020,
    /// ERROR_LOCK_VIOLATION 0x80070021). On Unix, flock conflicts produce generic IOException with no
    /// distinguishing HResult — so any IOException on Unix is treated as a lock conflict.
    /// This is only correct when called from Phase 1 (file open). Phase 2 (PID write) uses a separate
    /// catch block so that disk errors like ENOSPC are not misclassified as lock contention.
    /// Permission errors are handled separately via <see cref="UnauthorizedAccessException"/>.
    /// </remarks>
    private static bool IsSharingViolation(IOException ex)
    {
        const int ErrorSharingViolation = unchecked((int)0x80070020);
        const int ErrorLockViolation = unchecked((int)0x80070021);
        return ex.HResult is ErrorSharingViolation or ErrorLockViolation
            || !OperatingSystem.IsWindows();
    }
}

/// <summary>
/// Purpose: Enumerate why a host lock acquisition failed.
/// Complexity: Keeps startup decisions explicit without leaking IO error details.
/// </summary>
internal enum HostLockFailure
{
    /// <summary>Lock was acquired successfully.</summary>
    None,

    /// <summary>Another process holds the lock file (sharing violation).</summary>
    Locked,

    /// <summary>Filesystem permissions prevent access to the lock file.</summary>
    Unauthorized,

    /// <summary>Unexpected IO error (disk full, PID write failure, etc.).</summary>
    Error
}
