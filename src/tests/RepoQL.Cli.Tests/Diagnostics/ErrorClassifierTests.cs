using AwesomeAssertions;
using Grpc.Core;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.Protocol;

namespace RepoQL.Cli.Tests.Diagnostics;

/// <summary>
/// Purpose: Verify user errors do not trigger infrastructure diagnostics.
/// Complexity: Exercises classifier rules with in-memory exceptions only.
/// </summary>
internal sealed class ErrorClassifierTests
{
    [Test]
    public void InvalidArgument_IsNotInfrastructureError()
    {
        var rpc = new RpcException(new Status(StatusCode.InvalidArgument, "Parser Error: bad sql"));
        ErrorClassifier.IsInfrastructureError(rpc).Should().BeFalse();
    }

    [Test]
    public void DiagnosticsException_IsInfrastructureError()
    {
        var diagnostics = new RepoQlDiagnostics(
            RepoRoot: "repo",
            SocketPath: "/tmp/repoql.sock",
            ChannelState: "TransientFailure",
            HealthStatus: "WatchFaulted",
            HealthWatchFaulted: true,
            Host: null,
            RecoveryAttempted: true,
            CircuitBreakerOpen: false,
            CircuitBreakerFailures: 2,
            CircuitBreakerWindow: TimeSpan.FromMinutes(5));
        var timeout = new TimeoutException("boom");
        Action act = () => diagnostics.Throw("failed", timeout);
        var ex = act.Should().Throw<RepoQlDiagnosticsException>().Which;

        ErrorClassifier.IsInfrastructureError(ex).Should().BeTrue();
        ErrorClassifier.GetCleanMessage(ex).Should().Be("boom");
    }
}
