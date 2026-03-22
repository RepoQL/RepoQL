using AwesomeAssertions;
using RepoQL.Client.Diagnostics;
using RepoQL.Client.Host;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.Tests.Diagnostics;

/// <summary>
/// Purpose: Verify diagnostics collection captures local probe state for disk and .repoql directory health.
/// Complexity: Uses real temp directories and repo markers, with process-global cwd management.
/// </summary>
[NotInParallel(nameof(DiagnosticsCollectorTests))]
internal sealed class DiagnosticsCollectorTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Test]
    public async Task CollectAsync_RepoKnownAndRepoqlMissing_SetsDirectoryFalseAndDiskFreeMb()
    {
        var repoRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        var report = await CollectFromDirectoryAsync(repoRoot);

        report.RepoRoot.Should().Be(Path.GetFullPath(repoRoot));
        report.RepoQlDirectoryExists.Should().BeFalse();
        report.DiskFreeMb.Should().NotBeNull();
    }

    [Test]
    public async Task CollectAsync_RepoKnownAndRepoqlExists_SetsDirectoryTrueAndDiskFreeMb()
    {
        var repoRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        Directory.CreateDirectory(Path.Combine(repoRoot, ".repoql"));

        var report = await CollectFromDirectoryAsync(repoRoot);

        report.RepoRoot.Should().Be(Path.GetFullPath(repoRoot));
        report.RepoQlDirectoryExists.Should().BeTrue();
        report.DiskFreeMb.Should().NotBeNull();
        report.DiskFreeMb!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task CollectAsync_NoRepoDetected_LeavesDirectoryAndDiskProbesUnset()
    {
        var markerFreeDirectory = TryCreateMarkerFreeDirectory();
        if (markerFreeDirectory is null)
        {
            Skip.Test("Could not create a marker-free test directory on this system.");
            return;
        }

        var report = await CollectFromDirectoryAsync(markerFreeDirectory);

        report.RepoRoot.Should().BeNull();
        report.RepoQlDirectoryExists.Should().BeNull();
        report.DiskFreeMb.Should().BeNull();
    }

    [Test]
    public async Task CollectAsync_InMemoryHostStderrEmpty_ReadsStderrFallbackFile()
    {
        var repoRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var repoqlDirectory = Directory.CreateDirectory(Path.Combine(repoRoot, ".repoql"));
        var stderrPath = Path.Combine(repoqlDirectory.FullName, CrossSessionHostState.HostStderrFileName);
        File.WriteAllLines(stderrPath, Enumerable.Range(1, 60).Select(i => $"stderr-{i}"));

        var report = await CollectFromDirectoryAsync(
            repoRoot,
            () => new HostDiagnostics(Array.Empty<string>(), null, null, null, null, null, null));

        report.HostStderrTail.Should().BeEmpty();
        report.HostStderrFromFile.Should().NotBeNull();
        var lines = report.HostStderrFromFile!
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().HaveCount(50);
        lines[0].Should().Be("stderr-11");
        lines[^1].Should().Be("stderr-60");
    }

    [Test]
    public async Task CollectAsync_InMemoryHostStderrPresent_SkipsStderrFallbackFileProbe()
    {
        var repoRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        Directory.CreateDirectory(Path.Combine(repoRoot, ".repoql"));

        var report = await CollectFromDirectoryAsync(
            repoRoot,
            () => new HostDiagnostics(["in-memory stderr"], null, null, null, null, null, null));

        report.HostStderrTail.Should().ContainSingle().Which.Should().Be("in-memory stderr");
        report.HostStderrFromFile.Should().BeNull();
        report.ProbeFailures.Should().NotContain(x => x.StartsWith("host_stderr_file:", StringComparison.Ordinal));
    }

    [Test]
    public async Task CollectAsync_HostVersionFileExists_SetsHostVersionFile()
    {
        var repoRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var repoqlDirectory = Directory.CreateDirectory(Path.Combine(repoRoot, ".repoql"));
        var versionPath = Path.Combine(repoqlDirectory.FullName, CrossSessionHostState.HostVersionFileName);
        File.WriteAllText(versionPath, "9.9.9");

        var report = await CollectFromDirectoryAsync(
            repoRoot,
            () => new HostDiagnostics(Array.Empty<string>(), null, null, null, null, null, null));

        report.HostVersionFile.Should().Be("9.9.9");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort cleanup for test temp directories.
            }
        }
    }

    private async Task<DiagnosticReport> CollectFromDirectoryAsync(
        string directory,
        Func<HostDiagnostics>? hostDiagnosticsProvider = null)
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        var previousPwd = Environment.GetEnvironmentVariable("PWD");
        Directory.SetCurrentDirectory(directory);
        Environment.SetEnvironmentVariable("PWD", directory);

        try
        {
            var collector = new DiagnosticsCollector(hostDiagnosticsProvider);
            return await collector.CollectAsync(DiagnosticCollectionMode.Fast);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable("PWD", previousPwd);
        }
    }

    private string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"repoql-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        return directory;
    }

    private string? TryCreateMarkerFreeDirectory()
    {
        var directory = CreateTempDirectory();
        if (!RepoLocator.TryFindRepoRoot(directory, out _, out _, allowFallback: false))
            return directory;

        try
        {
            Directory.Delete(directory, recursive: true);
            _tempDirs.Remove(directory);
        }
        catch
        {
            // Best effort cleanup; if deletion fails we'll still return null and skip.
        }

        var driveRoot = Path.GetPathRoot(Path.GetTempPath());
        if (string.IsNullOrWhiteSpace(driveRoot))
            return null;

        var fallbackBase = Path.Combine(driveRoot, "repoql-markerfree-tests");
        try
        {
            Directory.CreateDirectory(fallbackBase);
        }
        catch
        {
            return null;
        }

        var fallbackDirectory = Path.Combine(fallbackBase, $"repoql-diagnostics-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(fallbackDirectory);
        }
        catch
        {
            return null;
        }

        _tempDirs.Add(fallbackDirectory);
        return RepoLocator.TryFindRepoRoot(fallbackDirectory, out _, out _, allowFallback: false)
            ? null
            : fallbackDirectory;
    }
}
