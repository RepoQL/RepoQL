using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Client.CommandImplementations;
using RepoQL.Client.Diagnostics;
using RepoQL.Protocol;

namespace RepoQL.Tests.CommandImplementations;

internal sealed class HostRestartCommandTests
{
    [Test]
    public async Task Execute_HappyPath_ReturnsSuccess()
    {
        var ops = A.Fake<HostRestartCommand.IHostRestartOperations>();
        var initial = CreateReport(
            socketConnectable: true,
            socketPath: "/tmp/repoql.sock",
            hostProcessId: 1234,
            hostRunning: true,
            healthOverall: "SERVING");
        var verification = CreateReport(
            socketConnectable: true,
            socketPath: "/tmp/repoql.sock",
            hostProcessId: 5678,
            hostRunning: true,
            healthOverall: "SERVING",
            hostStderrTail: ["phase: socket bind", "phase: ready"]);

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.TryShutdownHostAsync("/tmp/repoql.sock", A<CancellationToken>._))
            .Returns(Task.FromResult(HostRestartCommand.ShutdownAttempt.FromSuccess(1234)));
        A.CallTo(() => ops.InspectProcess(1234))
            .Returns(HostRestartCommand.ProcessInspection.RepoQl("repoql"));
        A.CallTo(() => ops.WaitForExitAsync(1234, A<TimeSpan>._, A<CancellationToken>._))
            .Returns(Task.FromResult(true));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostRestartCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostRestartCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._)).Returns(Task.CompletedTask);

        var command = new HostRestartCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Host restarted");
        result.Text.Should().Contain("verdict OK");
        result.Text.Should().Contain("Startup logs:");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .MustHaveHappened();
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._)).MustHaveHappened();
    }

    [Test]
    public async Task Execute_ProcessTerminationFailure_ReturnsStructuredEscalation()
    {
        var ops = A.Fake<HostRestartCommand.IHostRestartOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostProcessId: 4321,
            hostRunning: true,
            healthOverall: "SERVING");
        var verification = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.InspectProcess(4321))
            .Returns(HostRestartCommand.ProcessInspection.RepoQl("repoql"));
        A.CallTo(() => ops.WaitForExitAsync(4321, A<TimeSpan>._, A<CancellationToken>._))
            .Returns(Task.FromResult(false));
        A.CallTo(() => ops.TryTerminateRepoQlProcessAsync(4321, A<CancellationToken>._))
            .Returns(Task.FromResult(false));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostRestartCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostRestartCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("launch timeout"));

        var command = new HostRestartCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("process termination failed");
        result.Text.Should().Contain("pid: 4321");
        result.Text.Should().Contain("process: repoql");
        result.Text.Should().Contain("kill_attempted: yes");
        result.Text.Should().Contain("kill -9 4321");
    }

    [Test]
    public async Task Execute_SocketCleanupFailure_ReturnsStructuredEscalation()
    {
        var ops = A.Fake<HostRestartCommand.IHostRestartOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/stale.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");
        var verification = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/stale.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.CleanupSocket("/tmp/stale.sock"))
            .Returns(HostRestartCommand.CleanupResult.Failure("/tmp/stale.sock", "permission denied"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostRestartCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("launch timeout"));

        var command = new HostRestartCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("socket cleanup failed");
        result.Text.Should().Contain("socket: /tmp/stale.sock");
        result.Text.Should().Contain("error: permission denied");
        result.Text.Should().Contain("manual: rm /tmp/stale.sock");
    }

    [Test]
    public async Task Execute_SocketBindFailedAfterLaunch_ReturnsStructuredEscalation()
    {
        var ops = A.Fake<HostRestartCommand.IHostRestartOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");
        var verification = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING",
            socketBindSucceeded: false,
            socketBindError: "EACCES permission denied");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostRestartCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostRestartCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._)).Returns(Task.CompletedTask);

        var command = new HostRestartCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("socket bind failed");
        result.Text.Should().Contain("EACCES permission denied");
        result.Text.Should().Contain("Check permissions on .repoql/");
    }

    [Test]
    public async Task Execute_DatabaseLockedExternally_ReturnsStructuredEscalationAndSkipsLaunch()
    {
        var ops = A.Fake<HostRestartCommand.IHostRestartOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            dbLocked: true,
            dbLockHolderName: "DBeaver.exe",
            dbLockHolderPid: 8888,
            hostRunning: false,
            healthOverall: "NOT_SERVING");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .Returns(Task.FromResult(initial));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostRestartCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostRestartCommand.CleanupResult.Success(null));

        var command = new HostRestartCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("database locked by external process");
        result.Text.Should().Contain("DBeaver.exe (pid 8888)");
        result.Text.Should().Contain("Close DBeaver.exe to release the lock");
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task Execute_HostDidNotStart_ReturnsStructuredEscalationWithStderr()
    {
        var ops = A.Fake<HostRestartCommand.IHostRestartOperations>();
        var initial = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING");
        var verification = CreateReport(
            socketConnectable: false,
            socketPath: "/tmp/repoql.sock",
            hostRunning: false,
            healthOverall: "NOT_SERVING",
            hostStderrFromFile: "line-1\nline-2");

        A.CallTo(() => ops.CollectDiagnosticsAsync(DiagnosticCollectionMode.Fast, A<CancellationToken>._))
            .ReturnsNextFromSequence(Task.FromResult(initial), Task.FromResult(verification));
        A.CallTo(() => ops.CleanupSocket("/tmp/repoql.sock"))
            .Returns(HostRestartCommand.CleanupResult.Success("/tmp/repoql.sock"));
        A.CallTo(() => ops.CleanupPidFile("/repo"))
            .Returns(HostRestartCommand.CleanupResult.Success(null));
        A.CallTo(() => ops.ResetClientStateAsync()).Returns(Task.CompletedTask);
        A.CallTo(() => ops.TriggerLaunchAsync(A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("host launch timeout"));

        var command = new HostRestartCommand(ops);
        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("host didn't start");
        result.Text.Should().Contain("socket: /tmp/repoql.sock");
        result.Text.Should().Contain("launch_error: host launch timeout");
        result.Text.Should().Contain("line-2");
        result.Text.Should().Contain("Check .repoql/host.log");
    }

    private static DiagnosticReport CreateReport(
        bool? socketConnectable,
        string? socketPath,
        int? hostProcessId = null,
        bool? dbLocked = false,
        string? dbLockHolderName = null,
        int? dbLockHolderPid = null,
        bool? hostRunning = null,
        string? healthOverall = null,
        bool? socketBindSucceeded = null,
        string? socketBindError = null,
        IReadOnlyList<string>? hostStderrTail = null,
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
            DbLocked = dbLocked,
            DbLockHolderName = dbLockHolderName,
            DbLockHolderPid = dbLockHolderPid,
            HealthOverall = healthOverall,
            SocketBindSucceeded = socketBindSucceeded,
            SocketBindError = socketBindError,
            HostStderrTail = hostStderrTail ?? Array.Empty<string>(),
            HostStderrFromFile = hostStderrFromFile
        };
    }
}
