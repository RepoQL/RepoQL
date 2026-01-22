using AwesomeAssertions;

namespace RepoQL.Protocol.Tests;

/// <summary>
/// Purpose: Ensure diagnostics render consistently via ToString().
/// Complexity: Pure in-memory formatting checks.
/// </summary>
internal sealed class RepoQlDiagnosticsToStringTests
{
    private static readonly string[] HostLines = ["line one", "line two"];

    [Test]
    public void ToString_IncludesHostAndConnectionDetails()
    {
        var host = new HostDiagnostics(
            StderrTail: HostLines,
            ExitCode: 1,
            WorkingDirectory: "cwd",
            ExecutablePath: "exe",
            LaunchTime: new DateTime(2026, 1, 22, 10, 0, 0, DateTimeKind.Utc),
            ProcessId: 4242,
            HasExited: true);
        var diagnostics = new RepoQlDiagnostics(
            RepoRoot: "repo",
            SocketPath: "/tmp/repoql.sock",
            ChannelState: "Ready",
            HealthStatus: "Serving",
            HealthWatchFaulted: false,
            Host: host,
            RecoveryAttempted: true,
            CircuitBreakerOpen: false,
            CircuitBreakerFailures: 2,
            CircuitBreakerWindow: TimeSpan.FromMinutes(5));

        var output = diagnostics.ToString();
        output.Should().Contain("RepoQL diagnostics:");
        output.Should().Contain("repo: repo");
        output.Should().Contain("socket: /tmp/repoql.sock");
        output.Should().Contain("channel: Ready");
        output.Should().Contain("health: Serving");
        output.Should().Contain("host_pid: 4242");
        output.Should().Contain("host_exit_code: 1");
        output.Should().Contain("line one");
        output.Should().Contain("line two");
    }
}
