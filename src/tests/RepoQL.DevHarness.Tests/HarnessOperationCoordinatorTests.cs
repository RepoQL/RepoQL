using System;
using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessOperationCoordinatorTests
{
    [Test]
    public void TryAcquire_CreatesAndReleasesLock()
    {
        using var temp = new TempRepo();
        var now = new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero);
        var coordinator = new HarnessOperationCoordinator(temp.Path, clock: () => now, log: _ => { });

        var (handle, error) = coordinator.TryAcquire("sess_alpha", "building", now);

        error.Should().BeNull();
        handle.Should().NotBeNull();
        File.Exists(temp.GetLockPath()).Should().BeTrue();

        handle!.Dispose();
        File.Exists(temp.GetLockPath()).Should().BeFalse();
    }

    [Test]
    public void TryAcquire_ReturnsConflict_WhenLockIsActive()
    {
        using var temp = new TempRepo();
        var now = new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero);
        var coordinator = new HarnessOperationCoordinator(temp.Path, clock: () => now, log: _ => { });

        var (firstHandle, firstError) = coordinator.TryAcquire("sess_primary", "building", now);

        firstError.Should().BeNull();
        firstHandle.Should().NotBeNull();

        try
        {
            var (secondHandle, secondError) = coordinator.TryAcquire("sess_other", "building", now);

            secondHandle.Should().BeNull();
            secondError.Should().NotBeNull();
            secondError.Should().Contain("sess_primary");
            secondError.Should().Contain("building");
        }
        finally
        {
            firstHandle?.Dispose();
        }
    }

    [Test]
    public void TryAcquire_RemovesStaleLock()
    {
        using var temp = new TempRepo();
        var now = new DateTimeOffset(2026, 2, 5, 14, 30, 0, TimeSpan.Zero);
        var staleStart = now - TimeSpan.FromMinutes(6);
        var lockPath = temp.GetLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var payload = JsonSerializer.Serialize(new
        {
            session_id = "sess_old",
            operation = "building",
            started_at = HarnessTimestampFormatter.Format(staleStart)
        });
        File.WriteAllText(lockPath, payload);

        var coordinator = new HarnessOperationCoordinator(temp.Path, clock: () => now, log: _ => { });

        var (handle, error) = coordinator.TryAcquire("sess_new", "deploying", now);

        error.Should().BeNull();
        handle.Should().NotBeNull();
        var updated = File.ReadAllText(lockPath);
        using var doc = JsonDocument.Parse(updated);
        doc.RootElement.GetProperty("session_id").GetString().Should().Be("sess_new");
        doc.RootElement.GetProperty("operation").GetString().Should().Be("deploying");

        handle!.Dispose();
    }

    private sealed class TempRepo : IDisposable
    {
        public TempRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repoql_harness_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string GetLockPath()
            => System.IO.Path.Combine(Path, ".repoql", HarnessOperationCoordinator.LockFileName);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
