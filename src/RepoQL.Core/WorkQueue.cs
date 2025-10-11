using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace RepoQL.Core;

/// <summary>
/// Bounded work queue that prevents the same item being enqueued when already pending or inflight.
/// </summary>
#pragma warning disable CA1711
public sealed class WorkQueue<T> : IAsyncDisposable where T : notnull
#pragma warning restore CA1711
{
    private readonly Channel<T> _channel;
    private readonly ConcurrentDictionary<T, byte> _waitSet = new();
    private readonly Task[] _readers;
    private int _depth;
    public int Depth => _depth;
    public int MaxDepth { get; }
    private TaskCompletionSource<bool> _idleTcs = NewCompletedTcs();
    private readonly TaskCompletionSource<bool> _workersReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _startedReaders;

    /// <summary>Create a queue with a bounded capacity.</summary>
    public WorkQueue(string name, int capacity, int readers, Func<T, Task> processItem, CancellationToken cancellationToken, Meter meter)
    {
        QueueDepth = meter.CreateObservableGauge(
            $"repoql.queue.{name}.depth",
            () => _depth,
            unit: "items",
            description: "Current queue size");
        QueueCapacity = meter.CreateObservableGauge(
            $"repoql.queue.{name}.capacity",
            () => MaxDepth,
            unit: "items",
            description: "Maximum queue capacity");
        WorkersActive = meter.CreateObservableGauge(
            $"repoql.workers.{name}.active",
            () => _readers?.Count() ?? 0,
            unit: "workers",
            description: "Number of active worker threads");
        MaxDepth = capacity;
        var readerCount = readers;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = readers == 1
        });

        _readers = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
        {
            var startedNow = Interlocked.Increment(ref _startedReaders);
            if (startedNow == readerCount)
                _workersReadyTcs.TrySetResult(true);
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                await processItem(item);
                Complete(item);
            }
        }, cancellationToken)).ToArray();

    }

    public ObservableGauge<int> WorkersActive { get; set; }

    public ObservableGauge<int> QueueCapacity { get; set; }

    public ObservableGauge<int> QueueDepth { get; set; }

    /// <summary>Enqueue an item if not already pending. Removes on failure to allow retries.</summary>
    public async ValueTask EnqueueAsync(T item, CancellationToken ct)
    {
        if (!_waitSet.TryAdd(item, 0)) return;
        try
        {
            var newDepth = Interlocked.Increment(ref _depth);
            if (newDepth == 1)
            {
                // Transitioned from idle -> busy: create a fresh TCS
                Volatile.Write(ref _idleTcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch
        {
            Complete(item);
            throw;
        }
    }

    /// <summary>Mark the item as processed so it may be re-enqueued later.</summary>
    private void Complete(T item)
    {
        if (_waitSet.TryRemove(item, out _))
        {
            var newDepth = Interlocked.Decrement(ref _depth);
            if (newDepth == 0)
            {
                // Transitioned to idle: complete the TCS
                Volatile.Read(ref _idleTcs).TrySetResult(true);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _channel.Writer.Complete();
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // Channel already closed; ignore to allow graceful disposal
        }
        await Task.WhenAll(_readers);
    }

    /// <summary>
    ///     Returns a task that completes the next time the queue becomes idle
    ///     (i.e., has no pending or in-flight items). If already idle, completes immediately.
    /// </summary>
    public Task WhenIdleAsync() => Volatile.Read(ref _idleTcs).Task;

    /// <summary>
    ///     Returns a task that completes once all worker tasks have started running.
    ///     Useful in tests to avoid races before enqueueing multiple items.
    /// </summary>
    public Task WorkersReadyAsync() => _workersReadyTcs.Task;

    private static TaskCompletionSource<bool> NewCompletedTcs()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }
}