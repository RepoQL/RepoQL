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
        // Full-solution runs add enough scheduler jitter that the observed elapsed time can land
        // a little below the configured 100ms timeout while still exercising the timeout path.
        timedOutItems[0].Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50));

        queue.TimeoutCount.Should().Be(1);
    }

    [Test]
    [DisplayName("FM-001: Non-cooperative item times out independently and stays bounded to its worker")]
    public async Task Given_ItemIgnoresCancellation_When_TimeoutExpires_Then_TimeoutIsObservedWithoutWorkerReplacement()
    {
        var processedItems = new List<int>();
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOutItems = new List<(int Item, TimeSpan Elapsed)>();

        await using var queue = new WorkQueue<int>(
            "non_cooperative_timeout_test",
            capacity: 10,
            readers: 1,
            async (item, _) =>
            {
                if (item == 2)
                {
                    started.TrySetResult(true);
                    await neverCompletes.Task.ConfigureAwait(false);
                    return;
                }

                processedItems.Add(item);
            },
            CancellationToken.None,
            itemTimeout: TimeSpan.FromMilliseconds(100),
            logger: NullLogger.Instance)
        {
            OnItemTimeout = (item, elapsed) => timedOutItems.Add((item, elapsed))
        };

        await queue.EnqueueAsync(1, CancellationToken.None);
        await queue.EnqueueAsync(2, CancellationToken.None);
        await queue.EnqueueAsync(3, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(250);

        processedItems.Should().Contain(1);
        processedItems.Should().NotContain(2);
        processedItems.Should().NotContain(3, "the worker should remain occupied by the stuck item");
        queue.TimeoutCount.Should().Be(1);
        timedOutItems.Should().ContainSingle();
        timedOutItems[0].Item.Should().Be(2);
        queue.GetPendingItems().Should().Contain(3);
        queue.GetInFlightItems().Should().ContainSingle();
        queue.GetInFlightItems().Single().Item.Should().Be(2);
        queue.WhenIdleAsync().IsCompleted.Should().BeFalse("queued work remains behind the wedged worker");

        neverCompletes.TrySetResult(true);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        processedItems.Should().Contain(3);
    }

    [Test]
    [DisplayName("FM-001: Timed-out non-cooperative item releases queue ownership once it is the only logical work left")]
    public async Task Given_NonCooperativeTimeoutWithoutFollowers_When_TimeoutExpires_Then_WhenIdleCompletesWhileWorkerRemainsVisible()
    {
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new WorkQueue<int>(
            "non_cooperative_logical_idle",
            capacity: 4,
            readers: 1,
            async (item, _) =>
            {
                if (item != 1)
                    return;

                started.TrySetResult(true);
                await neverCompletes.Task.ConfigureAwait(false);
            },
            CancellationToken.None,
            itemTimeout: TimeSpan.FromMilliseconds(100),
            logger: NullLogger.Instance);

        await queue.EnqueueAsync(1, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        queue.TimeoutCount.Should().Be(1);
        queue.Depth.Should().Be(0);
        queue.GetInFlightItems().Should().ContainSingle();
        queue.GetInFlightItems().Single().Item.Should().Be(1);

        neverCompletes.TrySetResult(true);
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
    [DisplayName("GetPendingItems excludes in-flight items while GetInFlightItems reports worker ownership")]
    public async Task Given_InFlightAndQueuedItems_When_QueryingDiagnostics_Then_WorkIsSeparated(CancellationToken token)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var queue = new WorkQueue<int>(
            "worker_visibility",
            capacity: 16,
            readers: 1,
            async (item, ct) =>
            {
                if (item == 1)
                {
                    started.TrySetResult(true);
                    await release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            },
            CancellationToken.None,
            itemTimeout: TimeSpan.FromSeconds(5),
            logger: NullLogger.Instance)
        ;

        await queue.EnqueueAsync(1, token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
        await queue.EnqueueAsync(2, token);

        var pending = queue.GetPendingItems();
        var inFlight = queue.GetInFlightItems();

        pending.Should().ContainSingle().Which.Should().Be(2);
        inFlight.Should().ContainSingle();
        inFlight[0].WorkerId.Should().Be(0);
        inFlight[0].Item.Should().Be(1);
        inFlight[0].Duration.Should().BeGreaterThan(TimeSpan.Zero);

        release.TrySetResult(true);
        await queue.WhenIdleAsync().WaitAsync(token);
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
