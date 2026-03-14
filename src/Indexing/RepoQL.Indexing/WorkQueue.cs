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
/// Timeout detection runs independently from the worker, so non-cooperative work is still marked
/// timed out and released from queue ownership even if it never unwinds. The worker thread stays
/// occupied until the work returns, which bounds damage to the configured worker pool instead of
/// allowing hidden orphan work on the shared thread pool.
/// </para>
///
/// <para><strong>Concurrent Workers</strong></para>
/// <para>
/// Configurable worker count (typically <see cref="Environment.ProcessorCount"/>).
/// Each worker is a dedicated background thread that pulls from the bounded channel and runs
/// <c>processItem</c>. This isolates risky indexing work from ASP.NET and gRPC thread-pool usage.
/// </para>
///
/// <para><strong>Idle Detection</strong></para>
/// <para>
/// <see cref="WhenIdleAsync"/> completes when queue ownership drains (depth reaches zero).
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
    private readonly Thread[] _workers;
    private readonly Func<T, CancellationToken, Task> _processItem;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly Task _timeoutMonitor;
    private readonly IEqualityComparer<T> _comparer;
    private readonly TimeSpan? _itemTimeout;
    private readonly ILogger _logger;
    private readonly string _name;
    private int _depth;
    private int _busy;
    private int _timeoutCount;
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
    /// Invoked after an item fully leaves the queue's ownership (removed from dedupe set and depth).
    /// </summary>
    public Action<T>? OnItemCompleted { get; set; }

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
        _processItem = processItem;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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

        MaxDepth = capacity;
        _readerCount = Math.Max(1, readers);

        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _readerCount == 1
        });

        _workers = Enumerable.Range(0, _readerCount).Select(workerIndex =>
        {
            var worker = new Thread(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"RepoQL.WorkQueue.{name}.{workerIndex}"
            };
            worker.Start();
            return worker;
        }).ToArray();

        _timeoutMonitor = _itemTimeout is null
            ? Task.CompletedTask
            : Task.Run(() => MonitorTimeoutsAsync(_lifetimeCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Worker loop runs on a dedicated thread so risky synchronous or native work cannot starve the shared thread pool.
    /// </summary>
    private void WorkerLoop(int workerIndex)
    {
        var startedNow = Interlocked.Increment(ref _startedReaders);
        if (startedNow == _readerCount)
            _workersReadyTcs.TrySetResult(true);

        while (true)
        {
            T item;
            try
            {
                item = _channel.Reader.ReadAsync(_lifetimeCts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (Exception ex)
            {
                TryEnterTerminalFault(ex);
                break;
            }

            Interlocked.Increment(ref _busy);
            var startTime = Stopwatch.GetTimestamp();
            using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var inFlightInfo = new InFlightItem(item, workerIndex, DateTimeOffset.UtcNow, startTime, itemCts);
            _inFlightItems[workerIndex] = inFlightInfo;

            try
            {
                if (TryGetFatalError(out _))
                {
                    HandleItemTimeout(item, startTime, QueueFaultDisposition);
                    Complete(item, inFlightInfo);
                    continue;
                }

                _processItem(item, itemCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (inFlightInfo.IsTimedOut)
            {
                // Timed-out work is already accounted for by the monitor.
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorkQueue {QueueName} worker caught unhandled exception processing item", _name);
            }
            finally
            {
                _inFlightItems.TryRemove(workerIndex, out _);
                Interlocked.Decrement(ref _busy);
                Complete(item, inFlightInfo);
            }
        }
    }

    private async Task MonitorTimeoutsAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_itemTimeout);

        var pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(_itemTimeout.Value.TotalMilliseconds / 4, 50, 250));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var now = Stopwatch.GetTimestamp();
            foreach (var inFlight in _inFlightItems.Values)
            {
                if (inFlight.IsTimedOut)
                    continue;

                var elapsed = Stopwatch.GetElapsedTime(inFlight.StartTimestamp, now);
                if (elapsed < _itemTimeout.Value)
                    continue;

                if (!inFlight.TryMarkTimedOut())
                    continue;

                try
                {
                    inFlight.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                HandleItemTimeout(inFlight.Item, inFlight.StartTimestamp, TimedOutDisposition);
                Complete(inFlight.Item, inFlight);
            }
        }
    }

    private void HandleItemTimeout(T item, long startTimestamp, string disposition)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        _logger.LogWarning(
            "WorkQueue {QueueName} item {Disposition} after {ElapsedSeconds:F1}s (timeout={TimeoutSeconds:F1}s). Item: {Item}",
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

    public ObservableGauge<int> WorkersActive { get; }
    public ObservableGauge<int> QueueCapacity { get; }
    public ObservableGauge<int> QueueDepth { get; }
    public ObservableGauge<int> ItemTimeouts { get; }

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

    /// <summary>Mark the item as logically complete so it may be re-enqueued later.</summary>
    private void Complete(T item, InFlightItem? inFlight = null)
    {
        if (inFlight is not null && !inFlight.TryMarkLogicallyCompleted())
            return;

        if (_waitSet.TryRemove(item, out _))
        {
            lock (_queueStateLock)
            {
                if (_depth > 0)
                    _depth--;
                if (_depth == 0)
                {
                    _idleTcs.TrySetResult(true);
                }
            }

            try
            {
                OnItemCompleted?.Invoke(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorkQueue {QueueName} OnItemCompleted callback threw exception", _name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _channel.Writer.Complete(); } catch { }
        try { _lifetimeCts.Cancel(); } catch { }

        var deadline = Stopwatch.StartNew();
        foreach (var worker in _workers)
        {
            var remaining = TimeSpan.FromSeconds(2) - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            try
            {
                worker.Join(remaining);
            }
            catch (ThreadStateException)
            {
            }
        }

        try
        {
            await _timeoutMonitor.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _lifetimeCts.Dispose();
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
            _waitSet.Clear();
            _depth = 0;
            _idleTcs.TrySetResult(true);
        }

        _logger.LogCritical(
            fatalError,
            "WorkQueue {QueueName} entered terminal fault. Pending items will be abandoned while the queue drains.",
            _name);

        try
        {
            _channel.Writer.TryComplete(fatalError);
        }
        catch
        {
        }

        try
        {
            _lifetimeCts.Cancel();
        }
        catch
        {
        }

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
    /// Gets items currently queued for workers, excluding those already being processed.
    /// </summary>
    public IReadOnlyList<T> GetPendingItems()
    {
        var inFlight = new HashSet<T>(_inFlightItems.Values.Select(info => info.Item), _comparer);
        return _waitSet.Keys.Where(item => !inFlight.Contains(item)).ToList();
    }

    /// <summary>
    /// Gets items currently being processed by workers with their durations.
    /// Useful for diagnosing stuck items.
    /// </summary>
    public IReadOnlyList<WorkQueueInFlightItem<T>> GetInFlightItems()
    {
        var now = Stopwatch.GetTimestamp();
        return _inFlightItems.Values
            .OrderBy(info => info.WorkerId)
            .Select(info => new WorkQueueInFlightItem<T>(
                info.WorkerId,
                info.Item,
                info.StartedAtUtc,
                Stopwatch.GetElapsedTime(info.StartTimestamp, now)))
            .ToList();
    }

    /// <summary>
    /// Represents an item currently being processed by a worker.
    /// </summary>
    /// <summary>
    /// Mutable per-worker execution record so timeout monitoring and worker unwind can coordinate idempotently.
    /// </summary>
    private sealed class InFlightItem
    {
        private int _timedOut;
        private int _logicallyCompleted;

        public InFlightItem(T item, int workerId, DateTimeOffset startedAtUtc, long startTimestamp, CancellationTokenSource itemCancellation)
        {
            Item = item;
            WorkerId = workerId;
            StartedAtUtc = startedAtUtc;
            StartTimestamp = startTimestamp;
            ItemCancellation = itemCancellation;
        }

        public T Item { get; }
        public int WorkerId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public long StartTimestamp { get; }
        public CancellationTokenSource ItemCancellation { get; }
        public bool IsTimedOut => Volatile.Read(ref _timedOut) == 1;
        public bool TryMarkTimedOut() => Interlocked.Exchange(ref _timedOut, 1) == 0;
        public bool TryMarkLogicallyCompleted() => Interlocked.Exchange(ref _logicallyCompleted, 1) == 0;

        public void Cancel() => ItemCancellation.Cancel();
    }
}

public readonly record struct WorkQueueSnapshot(int Depth, int InProgress, int MaxDepth)
{
    public int Queued => Math.Max(0, Depth - InProgress);
}

public readonly record struct WorkQueueInFlightItem<T>(
    int WorkerId,
    T Item,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration);
