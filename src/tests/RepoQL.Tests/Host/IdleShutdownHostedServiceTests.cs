using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Client.Host;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Host;

internal sealed class IdleShutdownHostedServiceTests
{
    [Test]
    public async Task Given_ImplicitShutdownHangs_When_WatchdogExpires_Then_ForceTerminateIsInvoked()
    {
        ClearLeases();
        var lifetime = new TestHostApplicationLifetime();
        using var metrics = new HostMetrics();
        var state = new HostState
        {
            RepositoryPath = Path.GetTempPath(),
            ImplicitStart = true,
            StartedAtUtc = DateTime.UtcNow
        };

        var forceTerminateCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new IdleShutdownHostedService(
            lifetime,
            NullLogger<IdleShutdownHostedService>.Instance,
            state,
            metrics,
            pollInterval: TimeSpan.FromMilliseconds(10),
            leaseTtl: TimeSpan.FromSeconds(30),
            idleGrace: TimeSpan.FromMilliseconds(20),
            shutdownWatchdog: TimeSpan.FromMilliseconds(40),
            forceTerminate: () => forceTerminateCalled.TrySetResult(true));

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await forceTerminateCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lifetime.StopCalls.Should().Be(1);
        await service.StopAsync(CancellationToken.None);
        ClearLeases();
    }

    [Test]
    public async Task Given_HostStopsPromptly_When_WatchdogArmed_Then_ForceTerminateIsNotInvoked()
    {
        ClearLeases();
        var lifetime = new TestHostApplicationLifetime();
        using var metrics = new HostMetrics();
        var state = new HostState
        {
            RepositoryPath = Path.GetTempPath(),
            ImplicitStart = true,
            StartedAtUtc = DateTime.UtcNow
        };

        var forceTerminateCalled = 0;
        using var service = new IdleShutdownHostedService(
            lifetime,
            NullLogger<IdleShutdownHostedService>.Instance,
            state,
            metrics,
            pollInterval: TimeSpan.FromMilliseconds(10),
            leaseTtl: TimeSpan.FromSeconds(30),
            idleGrace: TimeSpan.FromMilliseconds(20),
            shutdownWatchdog: TimeSpan.FromMilliseconds(400),
            forceTerminate: () => Interlocked.Increment(ref forceTerminateCalled));

        await service.StartAsync(CancellationToken.None);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.SignalApplicationStopped();

        await Task.Delay(500);
        Volatile.Read(ref forceTerminateCalled).Should().Be(0);
        lifetime.StopCalls.Should().Be(1);

        await service.StopAsync(CancellationToken.None);
        ClearLeases();
    }

    private static void ClearLeases()
    {
        foreach (var lease in LeaseRegistry.Snapshot())
            LeaseRegistry.Remove(lease.ClientId);
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private int _stopCalls;
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public TaskCompletionSource<bool> StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCalls);
            StopRequested.TrySetResult(true);
        }

        public void SignalApplicationStopped() => _stopped.Cancel();
    }
}
