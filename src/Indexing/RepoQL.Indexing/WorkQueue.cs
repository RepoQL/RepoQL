using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Indexing;

/// <summary>
/// Bounded, deduplicated work queue with concurrent workers and backpressure.
/// Core primitive for hot-path and idle-processing pipelines.
/// </summary>
/// <typeparam name="T">Item type. Must be non-null and provide equality semantics for deduplication.</typeparam>
/// <remarks>
/// <para><strong>Purpose</strong></para>
/// <para>
/// Provides a bounded, concurrent work queue with deduplication, backpressure, and per-item timeout
/// protection. Prevents stuck items from blocking the entire pipeline (FM-001 mitigation).
/// </para>
///
/// <para><strong>Complexity</strong></para>
/// <para>
/// Contains thread-safe state management via <see cref="ConcurrentDictionary{TKey,TValue}"/> for deduplication,
/// <see cref="Channel{T}"/> for bounded queueing, and interlocked operations for counters. The per-item
/// timeout uses a wall-clock deadline plus cancellation tokens to combine shutdown and timeout signals. The rest of the system
/// is protected from this complexity via simple EnqueueAsync/WhenIdleAsync API.
/// </para>
///
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
/// <para><strong>Per-Item Timeout (FM-001 Mitigation)</strong></para>
/// <para>
/// Configurable timeout per item prevents stuck items from blocking the pipeline indefinitely.
/// When timeout fires, the item is logged, marked as timed out via <see cref="OnItemTimeout"/>,
/// and processing continues. This ensures epoch counters remain balanced and idle processing
/// can proceed.
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
    private const string TimedOutDisposition = "timed out";
    private const string QueueFaultDisposition = "was abandoned after queue fault";
    private readonly Channel<T> _channel;
    private readonly ConcurrentDictionary<T, byte> _waitSet;
    private readonly ConcurrentDictionary<int, InFlightItem> _inFlightItems = new();
    private readonly Task[] _readers;
    private readonly IEqualityComparer<T> _comparer;
    private readonly TimeSpan? _itemTimeout;
    private readonly ILogger _logger;
    private readonly string _name;
    private int _depth;
    private int _busy;
    private int _timeoutCount;
    private int _orphanedTasks;
    private readonly int _readerCount;
    private readonly object _queueStateLock = new();
    private Exception? _fatalError;

    public int Depth => Volatile.Read(ref _depth);
    public int MaxDepth { get; }

    /// <summary>
    /// Gets the number of items that have timed out since queue creation.
    /// </summary>
    public int TimeoutCount => Volatile.Read(ref _timeoutCount);

    private TaskCompletionSource<bool> _idleTcs = NewCompletedTcs();
    private readonly TaskCompletionSource<bool> _workersReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _startedReaders;

    /// <summary>
    /// Invoked when an item times out during processing.
    /// The delegate receives the item and the elapsed processing duration.
    /// Use this to perform cleanup (e.g., decrement epoch counters).
    /// </summary>
    public Action<T, TimeSpan>? OnItemTimeout { get; set; }

    /// <summary>
    /// Invoked once when the queue enters a terminal faulted state.
    /// </summary>
    public Action<Exception>? OnQueueFault { get; set; }

    /// <summary>
    /// Creates a new work queue with concurrent workers.
    /// </summary>
    /// <param name="name">Queue name for metrics and logging.</param>
    /// <param name="capacity">Maximum queue capacity. Backpressure applied when full.</param>
    /// <param name="readers">Number of concurrent worker tasks.</param>
    /// <param name="processItem">Delegate invoked to process each item.</param>
    /// <param name="cancellationToken">Token that signals shutdown.</param>
    /// <param name="itemTimeout">
    /// Optional per-item timeout. If an item takes longer than this duration, it is considered
    /// timed out, logged, and processing continues with the next item. If null, items can run
    /// indefinitely (original behavior).
    /// </param>
    /// <param name="meter">Optional meter for metrics. Created if not provided.</param>
    /// <param name="comparer">Optional equality comparer for deduplication.</param>
    /// <param name="logger">Optional logger for timeout warnings.</param>
    public WorkQueue(
        string name,
        int capacity,
        int readers,
        Func<T, CancellationToken, Task> processItem,
        CancellationToken cancellationToken,
        TimeSpan? itemTimeout = null,
        Meter? meter = null,
        IEqualityComparer<T>? comparer = null,
        ILogger? logger = null)
    {
        _name = name;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _waitSet = new ConcurrentDictionary<T, byte>(_comparer);
        _itemTimeout = itemTimeout;
        _logger = logger ?? NullLogger.Instance;

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
        ItemTimeouts = meter.CreateObservableGauge(
            $"repoql.queue.{name}.timeouts",
            () => Volatile.Read(ref _timeoutCount),
            unit: "items",
            description: "Number of items that timed out during processing");
        OrphanedTasks = meter.CreateObservableGauge(
            $"repoql.queue.{name}.orphaned",
            () => Volatile.Read(ref _orphanedTasks),
            unit: "tasks",
            description: "Number of timed-out tasks still holding thread pool threads");

        MaxDepth = capacity;
        _readerCount = Math.Max(1, readers);

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _readerCount == 1
        });

        _readers = Enumerable.Range(0, _readerCount).Select(workerIndex => Task.Run(async () =>
        {
            var startedNow = Interlocked.Increment(ref _startedReaders);
            if (startedNow == _readerCount)
                _workersReadyTcs.TrySetResult(true);

            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _busy);
                var startTime = Stopwatch.GetTimestamp();
                var inFlightInfo = new InFlightItem(item, startTime);
                _inFlightItems[workerIndex] = inFlightInfo;

                try
                {
                    await ProcessItemWithTimeoutAsync(item, processItem, cancellationToken, startTime).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // FM-006 mitigation: catch unhandled exceptions to prevent worker death
                    _logger.LogError(ex, "WorkQueue {QueueName} worker caught unhandled exception processing item", _name);
                }
                finally
                {
                    _inFlightItems.TryRemove(workerIndex, out _);
                    Interlocked.Decrement(ref _busy);
                    Complete(item);
                }
            }
        }, cancellationToken)).ToArray();
    }

    /// <summary>
    /// Maximum orphaned tasks (timed-out but still holding thread pool threads) before
    /// the queue enters a terminal fault. Prevents uncapped thread pool growth.
    /// </summary>
    private int MaxOrphanedTasks => _readerCount * 2;

    /// <summary>
    /// Processes an item with optional timeout.
    /// When orphaned tasks accumulate (thread pool starvation risk), the queue fails fast
    /// and abandons pending items through the normal timeout cleanup path.
    /// </summary>
    private async Task ProcessItemWithTimeoutAsync(
        T item,
        Func<T, CancellationToken, Task> processItem,
        CancellationToken cancellationToken,
        long startTimestamp)
    {
        if (_itemTimeout is null)
        {
            // No timeout configured - original behavior
            await processItem(item, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TryGetFatalError(out _))
        {
            HandleItemTimeout(item, startTimestamp, QueueFaultDisposition);
            return;
        }

        // If too many orphaned tasks are holding thread pool threads, fail the queue
        // and abandon pending items through the normal timeout cleanup path.
        if (Volatile.Read(ref _orphanedTasks) >= MaxOrphanedTasks)
        {
            var fatalError = new InvalidOperationException(
                $"WorkQueue {_name} entered terminal fault after reaching orphan pressure " +
                $"({Volatile.Read(ref _orphanedTasks)} orphaned tasks, limit {MaxOrphanedTasks}).");
            TryEnterTerminalFault(fatalError);
            HandleItemTimeout(item, startTimestamp, QueueFaultDisposition);
            return;
        }

        // Normal path: process on a separate Task.Run so the worker can move on after timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var processingTask = Task.Run(() => processItem(item, timeoutCts.Token), CancellationToken.None);

        try
        {
            await processingTask.WaitAsync(_itemTimeout.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            HandleItemTimeout(item, startTimestamp, TimedOutDisposition);
            TrackOrphanedTask(processingTask, item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timeoutCts.Cancel();
            TrackOrphanedTask(processingTask, item);
            throw;
        }
    }

    private void HandleItemTimeout(T item, long startTimestamp, string disposition)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        _logger.LogWarning(
            "WorkQueue {QueueName} item {Disposition} after {ElapsedSeconds:F1}s (timeout={TimeoutSeconds:F0}s). Item: {Item}",
            _name,
            disposition,
            elapsed.TotalSeconds,
            _itemTimeout?.TotalSeconds ?? 0,
            item);

        // Invoke timeout callback so caller can clean up (e.g., decrement epoch counters).
        // This runs BEFORE incrementing the timeout counter so that cleanup is complete
        // before the count becomes observable to consumers polling TimeoutCount.
        try
        {
            OnItemTimeout?.Invoke(item, elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WorkQueue {QueueName} OnItemTimeout callback threw exception", _name);
        }

        Interlocked.Increment(ref _timeoutCount);
    }

    /// <summary>
    /// Tracks an orphaned task (timed out but still running, holding a thread pool thread).
    /// When the task eventually completes, decrements the orphan counter.
    /// </summary>
    private void TrackOrphanedTask(Task processingTask, T item)
    {
        Interlocked.Increment(ref _orphanedTasks);
        _ = processingTask.ContinueWith(
            t =>
            {
                Interlocked.Decrement(ref _orphanedTasks);
                if (t.IsFaulted)
                    _logger.LogDebug(t.Exception, "WorkQueue {QueueName} orphaned task faulted. Item: {Item}", _name, item);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public ObservableGauge<int> WorkersActive { get; }
    public ObservableGauge<int> QueueCapacity { get; }
    public ObservableGauge<int> QueueDepth { get; }
    public ObservableGauge<int> ItemTimeouts { get; }
    public ObservableGauge<int> OrphanedTasks { get; }

    /// <summary>Enqueue an item if not already pending. Removes on failure to allow retries.</summary>
    public async ValueTask<bool> EnqueueAsync(T item, CancellationToken ct)
    {
        ThrowIfFaulted();

        if (!_waitSet.TryAdd(item, 0))
            return false;

        try
        {
            lock (_queueStateLock)
            {
                if (_fatalError is not null)
                {
                    _waitSet.TryRemove(item, out _);
                    throw _fatalError;
                }

                if (_depth == 0)
                {
                    _idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _depth++;
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
            lock (_queueStateLock)
            {
                _depth--;
                if (_depth == 0)
                {
                    _idleTcs.TrySetResult(true);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _channel.Writer.Complete(); } catch { }

        // Wait up to 2 seconds for readers to finish gracefully, then give up
        // The cancellation token should have already been triggered, so readers
        // should exit quickly. If they're stuck in a long operation, we don't
        // want to block shutdown indefinitely.
        var allReaders = Task.WhenAll(_readers);
        var completed = await Task.WhenAny(allReaders, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (completed != allReaders)
        {
            // Readers didn't finish in time - log but don't block shutdown
            // The process is exiting anyway, so orphaned tasks will be cleaned up
        }
    }

    /// <summary>Completes the next time the queue has no pending or in-flight items.</summary>
    public Task WhenIdleAsync()
    {
        lock (_queueStateLock)
        {
            return _idleTcs.Task;
        }
    }

    /// <summary>Completes once all worker tasks have started running.</summary>
    public Task WorkersReadyAsync() => _workersReadyTcs.Task;

    private static TaskCompletionSource<bool> NewCompletedTcs()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }

    private bool TryGetFatalError(out Exception? fatalError)
    {
        lock (_queueStateLock)
        {
            fatalError = _fatalError;
            return fatalError is not null;
        }
    }

    private void ThrowIfFaulted()
    {
        Exception? fatalError;
        lock (_queueStateLock)
        {
            fatalError = _fatalError;
        }

        if (fatalError is not null)
            throw fatalError;
    }

    private void TryEnterTerminalFault(Exception fatalError)
    {
        ArgumentNullException.ThrowIfNull(fatalError);

        Action<Exception>? callback = null;
        lock (_queueStateLock)
        {
            if (_fatalError is not null)
                return;

            _fatalError = fatalError;
            callback = OnQueueFault;
        }

        _logger.LogCritical(
            fatalError,
            "WorkQueue {QueueName} entered terminal fault. Pending items will be abandoned while the queue drains.",
            _name);

        if (callback is null)
            return;

        try
        {
            callback(fatalError);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WorkQueue {QueueName} OnQueueFault callback threw exception", _name);
        }
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

    /// <summary>
    /// Gets items currently being processed by workers with their durations.
    /// Useful for diagnosing stuck items.
    /// </summary>
    public IReadOnlyList<(T Item, TimeSpan Duration)> GetInFlightItems()
    {
        var now = Stopwatch.GetTimestamp();
        return _inFlightItems.Values
            .Select(info => (info.Item, Stopwatch.GetElapsedTime(info.StartTimestamp, now)))
            .ToList();
    }

    /// <summary>
    /// Represents an item currently being processed by a worker.
    /// </summary>
    private readonly record struct InFlightItem(T Item, long StartTimestamp);
}

public readonly record struct WorkQueueSnapshot(int Depth, int InProgress, int MaxDepth)
{
    public int Queued => Math.Max(0, Depth - InProgress);
}
