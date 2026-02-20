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
        output.Should().Contain("RepoQL: DOWN");
        output.Should().Contain("problems:");
        output.Should().Contain("Stale socket");
        output.Should().Contain("Host not ready");
        output.Should().Contain("Previous host crashed");
        output.Should().Contain("status: no connection");
        output.Should().Contain("host: v1.2.3");
        output.Should().Contain("repo: repo");
        output.Should().Contain("host log:");
        output.Should().Contain("- ERROR: boom");
    }

    [Test]
    public void ToString_ReportsHangingRequestsAsProblem()
    {
        var report = new DiagnosticReport
        {
            TimestampUtc = new DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero),
            SocketConnectable = true,
            HealthOverall = "SERVING",
            RpcActiveRequests = 2,
            RpcHangingRequests = 1,
            RpcHangThresholdMs = 30_000,
            RpcOldestRequestAgeMs = 65_000,
            RpcOldestRequestMethod = "/repoql.v1.RepoQL/ExecuteRawQuery"
        };

        var output = report.ToString();
        output.Should().Contain("RepoQL: DEGRADED");
        output.Should().Contain("Requests hanging");
        output.Should().Contain("hanging=1");
        output.Should().Contain("oldest_method=/repoql.v1.RepoQL/ExecuteRawQuery");
    }
}
