using AwesomeAssertions;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

/// <summary>
/// Purpose: Verify host cross-session state files are written deterministically at startup.
/// Complexity: Uses temporary repositories and direct writer calls without starting the full host.
/// </summary>
[NotInParallel(nameof(CrossSessionHostStateTests))]
internal sealed class CrossSessionHostStateTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Test]
    public void TryWriteHostVersionFile_WritesSingleLineVersion()
    {
        var repoRoot = CreateTempDirectory();

        var success = CrossSessionHostState.TryWriteHostVersionFile(
            repoRoot,
            "1.4.1",
            out var versionPath,
            out var error);

        success.Should().BeTrue();
        error.Should().BeNull();
        File.Exists(versionPath).Should().BeTrue();
        File.ReadAllText(versionPath).Should().Be("1.4.1");
    }

    [Test]
    public void HostStderrFileMirror_WritesIncrementally_AndKeepsLast200Lines()
    {
        var repoRoot = CreateTempDirectory();
        var stderrPath = CrossSessionHostState.GetHostStderrPath(repoRoot);

        using (var mirror = new HostStderrFileMirror(stderrPath, TextWriter.Null))
        {
            mirror.WriteLine("stderr-1");
            File.ReadAllLines(stderrPath).Should().ContainSingle().Which.Should().Be("stderr-1");

            for (var i = 2; i <= 205; i++)
            {
                mirror.WriteLine($"stderr-{i}");
            }
        }

        var lines = File.ReadAllLines(stderrPath);
        lines.Should().HaveCount(CrossSessionHostState.HostStderrRingBufferLineCount);
        lines[0].Should().Be("stderr-6");
        lines[^1].Should().Be("stderr-205");
    }

    [Test]
    public void HostStderrFileMirror_TruncatesFileOnRestart()
    {
        var repoRoot = CreateTempDirectory();
        var stderrPath = CrossSessionHostState.GetHostStderrPath(repoRoot);

        using (var firstStartMirror = new HostStderrFileMirror(stderrPath, TextWriter.Null))
        {
            firstStartMirror.WriteLine("first-start");
        }
        File.ReadAllLines(stderrPath).Should().ContainSingle().Which.Should().Be("first-start");

        using (var secondStartMirror = new HostStderrFileMirror(stderrPath, TextWriter.Null))
        {
            File.ReadAllText(stderrPath).Should().BeEmpty();
            secondStartMirror.WriteLine("second-start");
        }

        File.ReadAllLines(stderrPath).Should().ContainSingle().Which.Should().Be("second-start");
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirs)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    private string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"repoql-cross-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        return directory;
    }
}
