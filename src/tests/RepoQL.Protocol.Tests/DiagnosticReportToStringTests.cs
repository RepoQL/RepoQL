namespace RepoQL.Protocol.Tests;

/// <summary>
/// Purpose: Ensure DiagnosticReport renders a compact, readable summary.
/// Complexity: Pure in-memory formatting checks.
/// </summary>
internal sealed class DiagnosticReportToStringTests
{
    [Test]
    public void ToString_FormatsFactsAndProblems()
    {
        var report = new DiagnosticReport
        {
            TimestampUtc = new DateTimeOffset(2026, 1, 23, 12, 0, 0, TimeSpan.Zero),
            ProcessId = 123,
            RepoRoot = "repo",
            CurrentDirectory = "cwd",
            Platform = "win32",
            Runtime = "net",
            RepoqlVersion = "1.2.3",
            SocketPath = "/tmp/repoql.sock",
            SocketExists = true,
            SocketConnectable = false,
            HealthOverall = "NOT_SERVING",
            HealthReason = "initial_indexing",
            HostRunning = false,
            HostLogTail = ["ERROR: boom"],
            Artifacts = new Dictionary<string, string> { ["services-start.json"] = "DEGRADED" },
            ProbeFailures = ["health: timeout"]
        };

        var output = report.ToString();
        output.Should().Contain("RepoQL diagnostics");
        output.Should().Contain("problems:");
        output.Should().Contain("Host not ready");
        output.Should().Contain("facts:");
        output.Should().Contain("environment:");
        output.Should().Contain("socket:");
        output.Should().Contain("health:");
        output.Should().Contain("artifacts:");
        output.Should().Contain("probe_failures:");
        output.Should().Contain("host log (last 1):");
    }
}
