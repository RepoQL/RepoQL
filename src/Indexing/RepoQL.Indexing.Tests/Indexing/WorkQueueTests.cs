using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

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
    [DisplayName("FM-001: Non-cooperative timeout does not block subsequent items")]
    public async Task Given_ItemIgnoresCancellation_When_ItemTimesOut_Then_WorkerContinues()
    {
        // Arrange
        var processedItems = new List<int>();
        var timedOutItems = new List<int>();
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new WorkQueue<int>(
            "non_cooperative_timeout_test",
            capacity: 10,
            readers: 1,
            async (item, _) =>
            {
                if (item == 2)
                {
                    // Simulate non-cooperative processor: ignores cancellation and never returns.
                    await neverCompletes.Task.ConfigureAwait(false);
                    return;
                }

                processedItems.Add(item);
            },
            CancellationToken.None,
            itemTimeout: TimeSpan.FromMilliseconds(100),
            logger: NullLogger.Instance)
        {
            OnItemTimeout = (item, _) => timedOutItems.Add(item)
        };

        // Act
        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None);
        await queue.EnqueueAsync(3, CancellationToken.None);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        processedItems.Should().Contain(1);
        processedItems.Should().Contain(3);
        processedItems.Should().NotContain(2);
        timedOutItems.Should().ContainSingle().Which.Should().Be(2);
        queue.TimeoutCount.Should().Be(1);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("WhenIdleAsync returns a fresh task for each busy cycle")]
    public async Task Given_RepeatedBusyCycles_When_CapturingWhenIdle_Then_EachTaskCompletesAfterItsCycle(CancellationToken token)
    {
        var releaseByItem = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();
        var startedByItem = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();

        await using var queue = new WorkQueue<int>(
            "idle_race_regression",
            capacity: 16,
            readers: 1,
            async (item, ct) =>
            {
                startedByItem[item].TrySetResult(true);
                await releaseByItem[item].Task.WaitAsync(ct).ConfigureAwait(false);
            },
            CancellationToken.None,
            logger: NullLogger.Instance);

        Task? previousIdleTask = null;
        for (var item = 0; item < 25; item++)
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            startedByItem[item] = started;
            releaseByItem[item] = release;

            await queue.EnqueueAsync(item, token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), token);

            var idleTask = queue.WhenIdleAsync();
            idleTask.IsCompleted.Should().BeFalse();
            if (previousIdleTask is not null)
            {
                idleTask.Should().NotBeSameAs(previousIdleTask);
            }

            release.TrySetResult(true);
            await idleTask.WaitAsync(TimeSpan.FromSeconds(2), token);

            previousIdleTask = idleTask;
        }

        queue.Depth.Should().Be(0);
        queue.WhenIdleAsync().IsCompleted.Should().BeTrue();
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Orphan pressure faults the queue and abandons remaining items")]
    public async Task Given_OrphanPressure_When_LimitReached_Then_QueueFaultsAndFutureEnqueueFails(CancellationToken token)
    {
        var processedItems = new ConcurrentBag<int>();
        var timedOutItems = new ConcurrentBag<int>();
        var queueFaults = new ConcurrentBag<Exception>();
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new WorkQueue<int>(
            "orphan_pressure",
            capacity: 16,
            readers: 1,
            async (item, _) =>
            {
                if (item is 2 or 3)
                {
                    await neverCompletes.Task.ConfigureAwait(false);
                    return;
                }

                processedItems.Add(item);
            },
            CancellationToken.None,
            itemTimeout: TimeSpan.FromMilliseconds(100),
            logger: NullLogger.Instance)
        {
            OnItemTimeout = (item, _) => timedOutItems.Add(item),
            OnQueueFault = ex => queueFaults.Add(ex)
        };

        foreach (var item in Enumerable.Range(1, 6))
        {
            await queue.EnqueueAsync(item, token);
        }

        await queue.WhenIdleAsync().WaitAsync(token);

        processedItems.Should().Contain(1);
        queueFaults.Should().ContainSingle();
        queueFaults.Single().Message.Should().Contain("terminal fault");
        timedOutItems.Should().BeEquivalentTo([2, 3, 4, 5, 6]);
        queue.TimeoutCount.Should().Be(5);

        Func<Task> act = async () => await queue.EnqueueAsync(99, token);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal fault*");
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
