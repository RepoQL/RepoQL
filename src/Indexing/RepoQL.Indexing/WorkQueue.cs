using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace RepoQL.Indexing;

/// <summary>
/// Bounded, deduplicated work queue with concurrent workers and backpressure.
/// Core primitive for hot-path and idle-processing pipelines.
/// </summary>
/// <typeparam name="T">Item type. Must be non-null and provide equality semantics for deduplication.</typeparam>
/// <remarks>
/// <para><strong>Deduplication</strong></para>
/// <para>
/// Maintains <c>_waitSet</c> (<see cref="ConcurrentDictionary{TKey,TValue}"/>) of items
/// currently pending or in-flight. <see cref="EnqueueAsync"/> returns false if item already queued.
/// </para>
/// <code>
/// await queue.EnqueueAsync(item); // Returns true
/// await queue.EnqueueAsync(item); // Returns false (already pending)
/// // ... item processes ...
/// await queue.EnqueueAsync(item); // Returns true (processing complete)
/// </code>
///
/// <para><strong>Backpressure</strong></para>
/// <para>
/// Uses <see cref="Channel{T}"/> with <see cref="BoundedChannelOptions.FullMode"/> = Wait.
/// When capacity reached, <see cref="EnqueueAsync"/> blocks until space available.
/// Prevents unbounded memory growth.
/// </para>
///
/// <para><strong>Concurrent Workers</strong></para>
/// <para>
/// Configurable worker count (typically <see cref="Environment.ProcessorCount"/>).
/// Each worker pulls from channel and calls <c>processItem</c> delegate.
/// </para>
///
/// <para><strong>Idle Detection</strong></para>
/// <para>
/// <see cref="WhenIdleAsync"/> completes when queue drains (depth reaches zero).
/// Returns new <see cref="Task"/> each time (not reusable - create fresh wait after each idle).
/// </para>
///
/// <para><strong>Observability</strong></para>
/// <para>
/// Exposes <see cref="ObservableGauge{T}"/> metrics: <see cref="QueueDepth"/>,
/// <see cref="QueueCapacity"/>, <see cref="WorkersActive"/>.
/// </para>
/// </remarks>
#pragma warning disable CA1711
public sealed class WorkQueue<T> : IAsyncDisposable where T : notnull
#pragma warning restore CA1711
{
    private readonly Channel<T> _channel;
    private readonly ConcurrentDictionary<T, byte> _waitSet;
    private readonly Task[] _readers;
    private readonly IEqualityComparer<T> _comparer;
    private int _depth;
    private int _busy;
    private readonly int _readerCount;

    public int Depth => Volatile.Read(ref _depth);
    public int MaxDepth { get; }

    private TaskCompletionSource<bool> _idleTcs = NewCompletedTcs();
    private readonly TaskCompletionSource<bool> _workersReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _startedReaders;

    public WorkQueue(
        string name,
        int capacity,
        int readers,
        Func<T, CancellationToken, Task> processItem,
        CancellationToken cancellationToken,
        Meter? meter = null,
        IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _waitSet = new ConcurrentDictionary<T, byte>(_comparer);

        meter ??= new Meter($"RepoQL.WorkQueue.{name}");
        QueueDepth = meter.CreateObservableGauge(
            $"repoql.queue.{name}.depth",
            () => Volatile.Read(ref _depth),
            unit: "items",
            description: "Current queue size");
        QueueCapacity = meter.CreateObservableGauge(
            $"repoql.queue.{name}.capacity",
            () => MaxDepth,
            unit: "items",
            description: "Maximum queue capacity");
        WorkersActive = meter.CreateObservableGauge(
            $"repoql.workers.{name}.active",
            () => Volatile.Read(ref _busy),
            unit: "workers",
            description: "Number of workers currently processing items");

        MaxDepth = capacity;
        _readerCount = Math.Max(1, readers);

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _readerCount == 1
        });

        _readers = Enumerable.Range(0, _readerCount).Select(_ => Task.Run(async () =>
        {
            var startedNow = Interlocked.Increment(ref _startedReaders);
            if (startedNow == _readerCount)
                _workersReadyTcs.TrySetResult(true);

            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _busy);
                try
                {
                    await processItem(item, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _busy);
                    Complete(item);
                }
            }
        }, cancellationToken)).ToArray();
    }

    public ObservableGauge<int> WorkersActive { get; }
    public ObservableGauge<int> QueueCapacity { get; }
    public ObservableGauge<int> QueueDepth { get; }

    /// <summary>Enqueue an item if not already pending. Removes on failure to allow retries.</summary>
    public async ValueTask<bool> EnqueueAsync(T item, CancellationToken ct)
    {
        if (!_waitSet.TryAdd(item, 0))
            return false;

        try
        {
            var newDepth = Interlocked.Increment(ref _depth);
            if (newDepth == 1)
            {
                Volatile.Write(ref _idleTcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch
        {
            Complete(item);
            throw;
        }
        return true;
    }

    /// <summary>Mark the item as processed so it may be re-enqueued later.</summary>
    private void Complete(T item)
    {
        if (_waitSet.TryRemove(item, out _))
        {
            var newDepth = Interlocked.Decrement(ref _depth);
            if (newDepth == 0)
            {
                Volatile.Read(ref _idleTcs).TrySetResult(true);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _channel.Writer.Complete(); } catch { }
        await Task.WhenAll(_readers);
    }

    /// <summary>Completes the next time the queue has no pending or in-flight items.</summary>
    public Task WhenIdleAsync() => Volatile.Read(ref _idleTcs).Task;

    /// <summary>Completes once all worker tasks have started running.</summary>
    public Task WorkersReadyAsync() => _workersReadyTcs.Task;

    private static TaskCompletionSource<bool> NewCompletedTcs()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }
    public WorkQueueSnapshot CaptureSnapshot()
    {
        var depth = Volatile.Read(ref _depth);
        var busy = Math.Max(0, Volatile.Read(ref _busy));
        return new WorkQueueSnapshot(depth, busy, MaxDepth);
    }

    /// <summary>
    /// Gets all items currently in the queue (including those being processed).
    /// </summary>
    public IReadOnlyList<T> GetPendingItems() => _waitSet.Keys.ToList();
}

public readonly record struct WorkQueueSnapshot(int Depth, int InProgress, int MaxDepth)
{
    public int Queued => Math.Max(0, Depth - InProgress);
}
