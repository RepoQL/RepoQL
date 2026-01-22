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
}
