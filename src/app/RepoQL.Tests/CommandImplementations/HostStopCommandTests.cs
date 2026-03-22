using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Client.CommandImplementations;
using RepoQL.Client.Diagnostics;
using RepoQL.Protocol;

namespace RepoQL.Tests.CommandImplementations;

internal sealed class HostStopCommandTests
{
    [Test]
    public async Task Execute_HappyPath_ReturnsSuccess()
    {
        var ops = A.Fake<HostStopCommand.IHostStopOperations>();
        var initial = CreateReport(
            socketConnectable: true,
            socketPath: "/tmp/repoql.sock",
            hostProcessId: 1234,
            hostRunning: true,
            healthOverall: "SERVING");
        var verification = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.TryShutdownHostAsync("/tmp/repoql.sock", A<CancellationToken>._))
            .Returns(Task.FromResult(HostStopCommand.ShutdownAttempt.FromSuccess(1234)));
        A.CallTo(() => ops.InspectProcess(1234))
            .Returns(HostStopCommand.ProcessInspection.RepoQl("repoql"));
        A.CallTo(() => ops.WaitForExitAsync(1234, A<TimeSpan>._, A<CancellationToken>._))
            .Returns(Task.FromResult(true));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostStopCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostStopCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);

        var command = new HostStopCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Host stopped");
        result.Text.Should().Contain("previous PID 1234 stopped");
        A.CallTo(() => ops.ResetClientStateAsync()).MustHaveHappened();
    }

    [Test]
    public async Task Execute_AlreadyStopped_ReturnsSuccess()
    {
        var ops = A.Fake<HostStopCommand.IHostStopOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");
        var verification = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostStopCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostStopCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);

        var command = new HostStopCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Host already stopped");
        A.CallTo(() => ops.TryShutdownHostAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => ops.TryTerminateRepoQlProcessAsync(A<int>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task Execute_HostStillRunning_ReturnsStructuredEscalation()
    {
        var ops = A.Fake<HostStopCommand.IHostStopOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostProcessId: 4321,
            hostRunning: true,
            healthOverall: "SERVING");
        var verification = CreateReport(
            socketConnectable: true,
            socketPath: "/tmp/repoql.sock",
            hostProcessId: 4321,
            hostRunning: true,
            healthOverall: "SERVING",
            hostStderrFromFile: "line-1\nline-2");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.InspectProcess(4321))
            .Returns(HostStopCommand.ProcessInspection.RepoQl("repoql"));
        A.CallTo(() => ops.WaitForExitAsync(4321, A<TimeSpan>._, A<CancellationToken>._))
            .Returns(Task.FromResult(false));
        A.CallTo(() => ops.TryTerminateRepoQlProcessAsync(4321, A<CancellationToken>._))
            .Returns(Task.FromResult(true));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostStopCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostStopCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);

        var command = new HostStopCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("host still appears to be running");
        result.Text.Should().Contain("previous_pid: 4321 (killed)");
        result.Text.Should().Contain("line-2");
    }

    private static DiagnosticReport CreateReport(
        bool? socketConnectable,
        string? socketPath,
        int? hostProcessId = null,
        bool? hostRunning = null,
        string? healthOverall = null,
        string? hostStderrFromFile = null)
    {
        return new DiagnosticReport
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            RepoRoot = "/repo",
            SocketConnectable = socketConnectable,
            SocketPath = socketPath,
            HostProcessId = hostProcessId,
            HostRunning = hostRunning,
            HealthOverall = healthOverall,
            HostStderrTail = Array.Empty<string>(),
            HostStderrFromFile = hostStderrFromFile
        };
    }
}
