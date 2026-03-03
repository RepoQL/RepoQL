using AwesomeAssertions;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

/// <summary>
/// Purpose: Verify host lock acquisition enforces single-host startup semantics and PID embedding.
/// Complexity: Uses filesystem-level locking to confirm sharing violations, PID readability, and stale-file recovery.
/// </summary>
internal sealed class HostLockTests
{
    /// <summary>Only one process can hold the lock; second attempt gets Locked failure.</summary>
    [Test]
    public void HostLock_PreventsConcurrentAcquisition()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            using var first = HostLock.TryAcquire(repoRoot, out var firstFailure, out var firstError);
            first.Should().NotBeNull();
            firstFailure.Should().Be(HostLockFailure.None);
            firstError.Should().BeNull();

            using var second = HostLock.TryAcquire(repoRoot, out var secondFailure, out var secondError);
            second.Should().BeNull();
            secondFailure.Should().Be(HostLockFailure.Locked);
            secondError.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>Lock file contains the current process PID in "PID:nnn" format after acquisition.</summary>
    [Test]
    public void TryAcquire_WritesPidToLockFile()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            using var hostLock = HostLock.TryAcquire(repoRoot, out _, out _);
            hostLock.Should().NotBeNull();

            var found = HostLock.TryReadHolderPid(repoRoot, out var pid);
            found.Should().BeTrue();
            pid.Should().Be(Environment.ProcessId);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>FileShare.Read allows competing processes to read the PID while lock is held.</summary>
    [Test]
    public void TryReadHolderPid_ReadableWhileLockHeld()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            using var hostLock = HostLock.TryAcquire(repoRoot, out _, out _);
            hostLock.Should().NotBeNull();

            // Simulate concurrent process reading the PID while lock is held
            var found = HostLock.TryReadHolderPid(repoRoot, out var pid);
            found.Should().BeTrue("PID should be readable by concurrent processes while lock is held");
            pid.Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>Missing lock file returns false, not an exception.</summary>
    [Test]
    public void TryReadHolderPid_ReturnsFalse_WhenNoLockFile()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".repoql"));

        try
        {
            var found = HostLock.TryReadHolderPid(repoRoot, out var pid);
            found.Should().BeFalse();
            pid.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>Corrupt or non-PID content in lock file returns false, not a crash.</summary>
    [Test]
    public void TryReadHolderPid_ReturnsFalse_WhenContentMalformed()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        var repoqlDir = Path.Combine(repoRoot, ".repoql");
        Directory.CreateDirectory(repoqlDir);

        try
        {
            // Write garbage to the lock file
            File.WriteAllText(Path.Combine(repoqlDir, "host.lock"), "not a valid pid format");

            var found = HostLock.TryReadHolderPid(repoRoot, out var pid);
            found.Should().BeFalse("malformed content should not parse as a PID");
            pid.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>After a crash, the stale lock file (no handle held) can be overwritten by a new acquirer.</summary>
    [Test]
    public void StaleLockFile_CanBeReacquired()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        var repoqlDir = Path.Combine(repoRoot, ".repoql");
        Directory.CreateDirectory(repoqlDir);

        try
        {
            // Simulate crash aftermath: lock file with stale PID, no process holds the handle
            File.WriteAllText(Path.Combine(repoqlDir, "host.lock"), "PID:99999\n");

            using var acquired = HostLock.TryAcquire(repoRoot, out var failure, out var error);
            acquired.Should().NotBeNull("stale lock file with no holder should be acquirable");
            failure.Should().Be(HostLockFailure.None);
            error.Should().BeNull();

            // Verify PID was overwritten with current process
            var found = HostLock.TryReadHolderPid(repoRoot, out var pid);
            found.Should().BeTrue();
            pid.Should().Be(Environment.ProcessId);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
                Directory.Delete(repoRoot, recursive: true);
        }
    }
}
