using AwesomeAssertions;
using RepoQL.ConsoleApp.Logging;
using Serilog;

namespace RepoQL.Tests.Logging;

/// <summary>
/// Purpose: Verify host persistence logging writes to the expected file and rolls safely.
/// Complexity: Exercises Serilog file output via HostLogging without booting the full host.
/// </summary>
internal class HostLoggingTests
{
    [Test]
    public void HostLogging_Initialize_WritesAndRollsHostLog()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-hostlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);

        try
        {
            var (logger, logPath) = HostLogging.Initialize(repoRoot);
            var payload = new string('a', 100_000);
            for (var i = 0; i < 15; i++)
            {
                logger.Information("{Payload}", payload);
            }

            Log.CloseAndFlush();

            File.Exists(logPath).Should().BeTrue("host log file should be created during logging");
            var repoqlDir = Path.Combine(repoRoot, ".repoql");
            Directory.GetFiles(repoqlDir, "host.log*").Length.Should().BeLessThanOrEqualTo(2,
                "log rolling should retain at most two files");
        }
        finally
        {
            Log.CloseAndFlush();
            if (Directory.Exists(repoRoot))
            {
                Directory.Delete(repoRoot, recursive: true);
            }
        }
    }
}
