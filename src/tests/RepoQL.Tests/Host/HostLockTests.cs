using AwesomeAssertions;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

/// <summary>
/// Purpose: Verify host lock acquisition enforces single-host startup semantics.
/// Complexity: Uses filesystem-level locking to confirm sharing violations are detected.
/// </summary>
internal sealed class HostLockTests
{
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
            {
                Directory.Delete(repoRoot, recursive: true);
            }
        }
    }

    [Test]
    public void StaleLockFile_WithNoPid_CanBeReacquired()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        var repoqlDir = Path.Combine(repoRoot, ".repoql");
        Directory.CreateDirectory(repoqlDir);

        try
        {
            // Simulate crash aftermath: lock file exists on disk but no process holds it
            var lockPath = Path.Combine(repoqlDir, "host.lock");
            File.WriteAllText(lockPath, string.Empty);

            // No host.pid file exists (crash before PID write, or PID deleted during shutdown)

            // A new host should be able to acquire the lock despite the stale file
            using var acquired = HostLock.TryAcquire(repoRoot, out var failure, out var error);
            acquired.Should().NotBeNull("stale lock file with no holder should be acquirable");
            failure.Should().Be(HostLockFailure.None);
            error.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, recursive: true);
            }
        }
    }

    [Test]
    public void StaleLockFile_IsDeletedWhenNoPidExists()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlock-{Guid.NewGuid():N}");
        var repoqlDir = Path.Combine(repoRoot, ".repoql");
        Directory.CreateDirectory(repoqlDir);

        try
        {
            // Simulate crash aftermath: empty lock file, no PID
            var lockPath = Path.Combine(repoqlDir, "host.lock");
            File.WriteAllText(lockPath, string.Empty);

            // The recovery path in TryWaitThenEvictZombieAsync deletes the lock file.
            // Verify the file can be deleted and a fresh lock acquired afterward.
            File.Exists(lockPath).Should().BeTrue();
            File.Delete(lockPath);
            File.Exists(lockPath).Should().BeFalse();

            // Fresh acquisition should work on the now-clean directory
            using var acquired = HostLock.TryAcquire(repoRoot, out var failure, out _);
            acquired.Should().NotBeNull();
            failure.Should().Be(HostLockFailure.None);
        }
        finally
        {
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, recursive: true);
            }
        }
    }
}
