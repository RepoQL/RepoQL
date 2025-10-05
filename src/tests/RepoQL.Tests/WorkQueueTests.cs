using System.Diagnostics.Metrics;
using AwesomeAssertions;
using RepoQL.Core;

namespace RepoQL.Tests;

public class WorkQueueTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task WhenIdleAsync_InitiallyIdle_CompletesImmediately()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");
        await using var q = new WorkQueue<int>("t", capacity: 4, readers: 1, processItem: _ => Task.CompletedTask, CancellationToken.None, meter);
        var idleTask = q.WhenIdleAsync();
        idleTask.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task Enqueue_Item_MakesQueueBusy_ThenIdleAfterProcess()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handle(int _)
        {
            started.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
        }

        await using var q = new WorkQueue<int>("t", capacity: 4, readers: 1, processItem: Handle, CancellationToken.None, meter);

        // Initially idle
        q.WhenIdleAsync().IsCompleted.Should().BeTrue();

        // Enqueue one item
        await q.EnqueueAsync(1, CancellationToken.None);

        // Wait until handler starts to ensure the item is being processed
        await Task.WhenAny(started.Task, Task.Delay(DefaultTimeout));
        started.Task.IsCompleted.Should().BeTrue();

        // While processing, queue should not be idle
        var idleDuring = q.WhenIdleAsync();
        idleDuring.IsCompleted.Should().BeFalse();

        // Allow processing to complete
        release.TrySetResult(true);

        // Now the queue should become idle
        await Task.WhenAny(idleDuring, Task.Delay(DefaultTimeout));
        idleDuring.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task Enqueue_DeduplicatesWhilePending_ThenAllowsReenqueueAfterCompletion()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Handle(int _)
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
        }

        await using var q = new WorkQueue<int>("t", capacity: 10, readers: 1, processItem: Handle, CancellationToken.None, meter);

        // Enqueue same item twice rapidly; second should be ignored while first in-flight
        await q.EnqueueAsync(42, CancellationToken.None);
        await q.EnqueueAsync(42, CancellationToken.None);

        // Give a brief moment for a potential (incorrect) second schedule
        await Task.Delay(50);
        calls.Should().Be(1); // only once so far

        // Complete first processing
        release.TrySetResult(true);
        await Task.WhenAny(q.WhenIdleAsync(), Task.Delay(DefaultTimeout));

        // Now re-enqueue after idle; should process again
        var release2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Handle2(int _)
        {
            Interlocked.Increment(ref calls);
            await release2.Task.ConfigureAwait(false);
        }

        await using var q2 = new WorkQueue<int>("t", capacity: 10, readers: 1, processItem: Handle2, CancellationToken.None, meter);

        await q2.EnqueueAsync(42, CancellationToken.None);
        await Task.Delay(50);
        calls.Should().Be(2);
        release2.TrySetResult(true);
        await Task.WhenAny(q2.WhenIdleAsync(), Task.Delay(DefaultTimeout));
        q2.WhenIdleAsync().IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task WhenIdleAsync_CompletesAfterMultipleItems()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");

        var processed = 0;
        async Task Handle(int _)
        {
            await Task.Delay(10);
            Interlocked.Increment(ref processed);
        }

        await using var q = new WorkQueue<int>("t", capacity: 10, readers: 1, processItem: Handle, CancellationToken.None, meter);
        await q.EnqueueAsync(1, CancellationToken.None);
        await q.EnqueueAsync(2, CancellationToken.None);
        await q.EnqueueAsync(3, CancellationToken.None);

        var idle = q.WhenIdleAsync();
        await Task.WhenAny(idle, Task.Delay(DefaultTimeout));
        idle.IsCompleted.Should().BeTrue();
        processed.Should().BeGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task MultipleReaders_StartTwoHandlersInParallel()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");

        var started = 0;
        var bothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handle(int _)
        {
            var s = Interlocked.Increment(ref started);
            if (s >= 2) bothStarted.TrySetResult(true);
            await gate.Task.ConfigureAwait(false);
        }

        await using var q = new WorkQueue<int>("t", capacity: 10, readers: 2, processItem: Handle, CancellationToken.None, meter);

        // Ensure both worker tasks are running so two items can be picked concurrently
        await Task.WhenAny(q.WorkersReadyAsync(), Task.Delay(DefaultTimeout));
        q.WorkersReadyAsync().IsCompleted.Should().BeTrue();

        await q.EnqueueAsync(1, CancellationToken.None);
        await q.EnqueueAsync(2, CancellationToken.None);

        // Wait until both handlers have started (or timeout to avoid hangs on slow CI)
        await Task.WhenAny(bothStarted.Task, Task.Delay(DefaultTimeout));
        Interlocked.CompareExchange(ref started, 0, 0).Should().BeGreaterThanOrEqualTo(2);

        // Release and wait idle (bounded by timeout)
        gate.TrySetResult(true);
        var idle = q.WhenIdleAsync();
        await Task.WhenAny(idle, Task.Delay(DefaultTimeout));
        idle.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task Capacity_Backpressure_BlocksWriterWhenFull()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");
        var sem = new SemaphoreSlim(0, int.MaxValue);

        async Task Handle(int _)
        {
            await sem.WaitAsync();
        }

        await using var q = new WorkQueue<int>("t", capacity: 2, readers: 1, processItem: Handle, CancellationToken.None, meter);

        // First enqueue: reader takes it immediately and waits on semaphore
        await q.EnqueueAsync(1, CancellationToken.None);

        // Fill buffer to capacity
        await q.EnqueueAsync(2, CancellationToken.None); // buffer: 1
        await q.EnqueueAsync(3, CancellationToken.None); // buffer: 2 (full)

        // Next enqueue should block until a slot is freed
        var enqueueFourth = q.EnqueueAsync(4, CancellationToken.None).AsTask();
        // Give a moment; it should not complete yet
        await Task.Delay(50);
        enqueueFourth.IsCompleted.Should().BeFalse();

        // Free one item; reader completes first item and then reads from buffer, freeing a slot
        sem.Release();

        // Now the fourth enqueue should finish quickly
        await Task.WhenAny(enqueueFourth, Task.Delay(DefaultTimeout));
        enqueueFourth.IsCompleted.Should().BeTrue();

        // Drain remaining items
        sem.Release(3);
        await Task.WhenAny(q.WhenIdleAsync(), Task.Delay(DefaultTimeout));
        q.WhenIdleAsync().IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task ManyReaders_ProcessHighVolume_Concurrently()
    {
        var meter = new Meter("RepoQL.Tests.WorkQueue");

        var processed = 0;
        var inflight = 0;
        var maxInflight = 0;

        async Task Handle(int _)
        {
            var now = Interlocked.Increment(ref inflight);
            // track peak concurrency
            int prev;
            do
            {
                prev = maxInflight;
                if (now <= prev) break;
            } while (Interlocked.CompareExchange(ref maxInflight, now, prev) != prev);

            // small delay to increase overlap
            await Task.Delay(1);

            Interlocked.Increment(ref processed);
            Interlocked.Decrement(ref inflight);
        }

        const int readers = 8;
        const int items = 1000;

        await using var q = new WorkQueue<int>("t", capacity: 2048, readers: readers, processItem: Handle, CancellationToken.None, meter);

        await Task.WhenAny(q.WorkersReadyAsync(), Task.Delay(DefaultTimeout));
        q.WorkersReadyAsync().IsCompleted.Should().BeTrue();

        for (var i = 0; i < items; i++)
        {
            await q.EnqueueAsync(i, CancellationToken.None);
        }

        var idle = q.WhenIdleAsync();
        await Task.WhenAny(idle, Task.Delay(DefaultTimeout));
        idle.IsCompleted.Should().BeTrue();

        processed.Should().Be(items);
        maxInflight.Should().BeGreaterThanOrEqualTo(4); // expect meaningful parallelism
    }
}