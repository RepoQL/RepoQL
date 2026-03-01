using FakeItEasy;

namespace RepoQL.Protocol.Tests;

/// <summary>
/// Purpose: Verify DiagnosticReportProblems rules surface the expected diagnoses and guardrails.
/// Complexity: Uses in-memory reports and asserts against rendered output.
/// </summary>
internal sealed class DiagnosticReportProblemRulesTests
{
    [Test]
    public void ToString_ShowsSocketBindFailed_WhenBindReportFailed()
    {
        var report = CreateBaselineReport() with
        {
            SocketBindSucceeded = false,
            SocketBindError = "permission denied"
        };

        var output = report.ToString();

        output.Should().Contain("Socket bind failed");
        output.Should().Contain("socket_bind_error=permission denied");
        output.Should().Contain("Check permissions on the socket directory: permission denied");
    }

    [Test]
    public void ToString_DoesNotShowSocketBindFailed_WhenBindStatusUnknown()
    {
        var report = CreateBaselineReport() with
        {
            SocketBindSucceeded = null,
            SocketBindError = "permission denied"
        };

        var output = report.ToString();

        output.Should().NotContain("Socket bind failed");
    }

    [Test]
    public void ToString_UsesLastErrorLineAsCrashReason()
    {
        var hostLogTail = A.Fake<IReadOnlyList<string>>();
        A.CallTo(() => hostLogTail.Count).Returns(3);
        A.CallTo(() => hostLogTail[0]).Returns("[host] INFO starting");
        A.CallTo(() => hostLogTail[1]).Returns("[host] ERR first failure");
        A.CallTo(() => hostLogTail[2]).Returns("[host] ERROR final failure");

        var report = CreateBaselineReport() with
        {
            HostRunning = false,
            HostLogTail = hostLogTail
        };

        var output = report.ToString();

        output.Should().Contain("Previous host crashed");
        output.Should().Contain("crash_reason=[host] ERROR final failure");
        output.Should().NotContain("crash_reason=[host] ERR first failure");
    }

    [Test]
    public void ToString_CrashRuleDegradesGracefully_WhenNoErrorLineExists()
    {
        var report = CreateBaselineReport() with
        {
            HostRunning = false,
            HostLogTail =
            [
                "[host] INFO starting",
                "[host] WARN waiting"
            ]
        };

        var output = report.ToString();

        output.Should().Contain("Previous host crashed");
        output.Should().Contain("host_log=error");
        output.Should().NotContain("crash_reason=");
    }

    [Test]
    public void ToString_ShowsLowDiskSpace_WhenBelowThreshold()
    {
        var report = CreateBaselineReport() with { DiskFreeMb = 99 };

        var output = report.ToString();

        output.Should().Contain("Low disk space");
        output.Should().Contain("disk_free_mb=99");
        output.Should().Contain("Free disk space on the volume containing .repoql/ (99 MB remaining)");
    }

    [Test]
    public void ToString_DoesNotShowLowDiskSpace_WhenProbeValueMissing()
    {
        var report = CreateBaselineReport() with { DiskFreeMb = null };

        var output = report.ToString();

        output.Should().NotContain("Low disk space");
    }

    [Test]
    public void ToString_ShowsNoRepoqlDirectory_WhenRepoRootKnownAndDirectoryMissing()
    {
        var report = CreateBaselineReport() with
        {
            RepoRoot = "C:/Source/Repo",
            RepoQlDirectoryExists = false
        };

        var output = report.ToString();

        output.Should().Contain("No .repoql directory");
        output.Should().Contain("Run a RepoQL command to initialize the repository");
    }

    [Test]
    public void ToString_DoesNotShowNoRepoqlDirectory_WhenRepoRootUnknown()
    {
        var report = CreateBaselineReport() with
        {
            RepoRoot = null,
            RepoQlDirectoryExists = false
        };

        var output = report.ToString();

        output.Should().NotContain("No .repoql directory");
    }

    [Test]
    public void ToString_ShowsVersionMismatch_WhenHostVersionDiffers()
    {
        var report = CreateBaselineReport() with
        {
            RepoqlVersion = "1.4.1",
            HostVersionFile = "1.4.0"
        };

        var output = report.ToString();

        output.Should().Contain("Version mismatch");
        output.Should().Contain("client_version=1.4.1");
        output.Should().Contain("host_version=1.4.0");
        output.Should().Contain("Client v1.4.1, host was v1.4.0. Restart may resolve.");
    }

    [Test]
    public void ToString_DoesNotShowVersionMismatch_WhenVersionsMatch()
    {
        var report = CreateBaselineReport() with
        {
            RepoqlVersion = "1.4.1",
            HostVersionFile = "1.4.1"
        };

        var output = report.ToString();

        output.Should().NotContain("Version mismatch");
    }

    [Test]
    public void ToString_DoesNotShowVersionMismatch_WhenHostVersionMissing()
    {
        var report = CreateBaselineReport() with
        {
            RepoqlVersion = "1.4.1",
            HostVersionFile = null
        };

        var output = report.ToString();

        output.Should().NotContain("Version mismatch");
    }

    private static DiagnosticReport CreateBaselineReport()
        => new()
        {
            TimestampUtc = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero),
            SocketConnectable = true,
            HealthOverall = "SERVING"
        };
}
