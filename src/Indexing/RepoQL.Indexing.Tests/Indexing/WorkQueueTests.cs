using AwesomeAssertions;

namespace RepoQL.Indexing.Tests.Indexing;

internal class WorkQueueTests
{
    [Test]
    [DisplayName("CaptureSnapshot reflects queued and in-progress counts")]
    public async Task Given_ItemsInFlight_When_CapturingSnapshot_Then_ReportsState()
    {
        var processingGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new WorkQueue<int>(
            "snapshot",
            capacity: 4,
            readers: 1,
            async (_, ct) =>
            {
                startedSignal.TrySetResult(true);
                await processingGate.Task.WaitAsync(ct).ConfigureAwait(false);
            },
            CancellationToken.None);

        await queue.EnqueueAsync(1, CancellationToken.None);
        await startedSignal.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(2, CancellationToken.None);

        var snapshot = queue.CaptureSnapshot();

        snapshot.Depth.Should().Be(2);
        snapshot.InProgress.Should().Be(1);
        snapshot.Queued.Should().Be(1);

        processingGate.TrySetResult(true);
    }

    [Test]
    [DisplayName("Snapshot reports zero when queue drains")]
    public async Task Given_QueueIdle_When_CapturingSnapshot_Then_ReportsZero()
    {
        await using var queue = new WorkQueue<int>(
            "idle",
            capacity: 2,
            readers: 1,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        var snapshot = queue.CaptureSnapshot();

        snapshot.Depth.Should().Be(0);
        snapshot.InProgress.Should().Be(0);
        snapshot.Queued.Should().Be(0);
    }
}
