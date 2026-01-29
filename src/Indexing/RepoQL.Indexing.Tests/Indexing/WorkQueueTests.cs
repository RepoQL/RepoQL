using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Test]
    [DisplayName("FM-001: Item timeout fires callback and continues processing")]
    public async Task Given_ItemTimeout_When_ItemExceedsTimeout_Then_FiresCallbackAndContinues()
    {
        // Arrange
        var processedItems = new List<int>();
        var timedOutItems = new List<(int Item, TimeSpan Elapsed)>();
        var cts = new CancellationTokenSource();

        await using var queue = new WorkQueue<int>(
            "timeout_test",
            capacity: 10,
            readers: 1,
            async (item, ct) =>
            {
                if (item == 2)
                {
                    // This item will timeout
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
                processedItems.Add(item);
            },
            cts.Token,
            itemTimeout: TimeSpan.FromMilliseconds(100),
            logger: NullLogger.Instance)
        {
            OnItemTimeout = (item, elapsed) =>
            {
                timedOutItems.Add((item, elapsed));
            }
        };

        // Act
        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None); // This one will timeout
        await queue.EnqueueAsync(3, CancellationToken.None);

        // Wait for processing to complete
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        processedItems.Should().Contain(1);
        processedItems.Should().Contain(3);
        processedItems.Should().NotContain(2); // Item 2 timed out

        timedOutItems.Should().HaveCount(1);
        timedOutItems[0].Item.Should().Be(2);
        timedOutItems[0].Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90));

        queue.TimeoutCount.Should().Be(1);
    }

    [Test]
    [DisplayName("FM-001: Items process normally when no timeout configured")]
    public async Task Given_NoTimeout_When_ItemTakesLong_Then_ProcessesNormally()
    {
        // Arrange
        var processedItems = new List<int>();
        var cts = new CancellationTokenSource();

        await using var queue = new WorkQueue<int>(
            "no_timeout_test",
            capacity: 10,
            readers: 1,
            async (item, _) =>
            {
                if (item == 2)
                {
                    await Task.Delay(50); // Short delay, would timeout if configured
                }
                processedItems.Add(item);
            },
            cts.Token,
            itemTimeout: null); // No timeout

        // Act
        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None);
        await queue.EnqueueAsync(3, CancellationToken.None);

        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - all items should be processed
        processedItems.Should().BeEquivalentTo([1, 2, 3]);
        queue.TimeoutCount.Should().Be(0);
    }

    [Test]
    [DisplayName("GetInFlightItems returns items currently being processed")]
    public async Task Given_ItemsProcessing_When_GetInFlightItems_Then_ReturnsCurrentItems()
    {
        // Arrange
        var processingGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new WorkQueue<string>(
            "inflight_test",
            capacity: 4,
            readers: 1,
            async (_, ct) =>
            {
                startedSignal.TrySetResult(true);
                await processingGate.Task.WaitAsync(ct).ConfigureAwait(false);
            },
            CancellationToken.None);

        // Act
        await queue.EnqueueAsync("test-item", CancellationToken.None);
        await startedSignal.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var inFlight = queue.GetInFlightItems();

        // Assert
        inFlight.Should().HaveCount(1);
        inFlight[0].Item.Should().Be("test-item");
        inFlight[0].Duration.Should().BeGreaterThan(TimeSpan.Zero);

        processingGate.TrySetResult(true);
    }

    [Test]
    [DisplayName("FM-006: Unhandled exceptions don't kill workers")]
    public async Task Given_ProcessorThrows_When_Processing_Then_WorkerContinues()
    {
        // Arrange
        var processedItems = new List<int>();
        var cts = new CancellationTokenSource();

        await using var queue = new WorkQueue<int>(
            "exception_test",
            capacity: 10,
            readers: 1,
            (item, _) =>
            {
                if (item == 2)
                {
                    throw new InvalidOperationException("Simulated failure");
                }
                processedItems.Add(item);
                return Task.CompletedTask;
            },
            cts.Token,
            logger: NullLogger.Instance);

        // Act
        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None); // This one will throw
        await queue.EnqueueAsync(3, CancellationToken.None);

        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert - items 1 and 3 should be processed despite item 2 throwing
        processedItems.Should().Contain(1);
        processedItems.Should().Contain(3);
        processedItems.Should().NotContain(2);
    }
}
