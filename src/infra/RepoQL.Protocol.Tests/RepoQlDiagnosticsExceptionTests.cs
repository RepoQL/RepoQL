namespace RepoQL.Protocol.Tests;

/// <summary>
/// Purpose: Verify diagnostics exception preserves payload and inner error.
/// Complexity: Simple in-memory checks without host dependencies.
/// </summary>
internal sealed class RepoQlDiagnosticsExceptionTests
{
    [Test]
    public void DiagnosticsException_StoresPayloadAndInnerException()
    {
        var host = new HostDiagnostics(Array.Empty<string>(), 1, "cwd", "exe", DateTime.UtcNow, 42, true);
        var diagnostics = new RepoQlDiagnostics(
            RepoRoot: "repo",
            SocketPath: "/tmp/repoql.sock",
            ChannelState: "Ready",
            HealthStatus: "Serving",
            HealthWatchFaulted: false,
            Host: host,
            RecoveryAttempted: true,
            CircuitBreakerOpen: false,
            CircuitBreakerFailures: 1,
            CircuitBreakerWindow: TimeSpan.FromMinutes(5));

        var inner = new InvalidOperationException("boom");
        Action act = () => diagnostics.Throw("failed", inner);
        var ex = act.Should().Throw<RepoQlDiagnosticsException>().Which;

        ex.Diagnostics.Should().Be(diagnostics);
        ex.InnerException.Should().Be(inner);
        ex.Message.Should().Be("failed");
    }
}
