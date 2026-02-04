using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Contracts;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Hosting;

/// <summary>
/// Background service that orchestrates enumerating and watching repositories to feed the indexing engine.
/// </summary>
public sealed class RepoqlHost : BackgroundService
{
    private readonly CompositeFileSystem _fileSystem;
    private readonly Func<RawArtifact, IndexItemOptions, CancellationToken, Task> _enqueue;
    private readonly IAsyncDisposable? _engineLifetime;
    private readonly IIndexingCoordinator? _coordinator;
    private readonly IServiceDegradationTracker? _degradation;
    private readonly IUriFilter? _filter;
    private readonly UriRegistry? _uriRegistry;
    private readonly IOperationManager? _operationManager;
    private readonly RepoqlHostOptions _options;
    private readonly ILogger<RepoqlHost> _logger;

    private IFileSystemWatcher? _watcher;
    private IDisposable? _watcherSubscription;
    private Channel<RawArtifact>? _watcherChannel;
    private Task? _watcherPump;
    private volatile bool _isStopping;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWriteByUri = new(StringComparer.OrdinalIgnoreCase);
    private readonly PeriodicTimer _dirtyTimer = new(TimeSpan.FromSeconds(1));
    private Task? _dirtyScanLoop;
    private volatile bool _dirty;
    private int _activeEnqueue;
    private volatile bool _watchingEnabled;
    private bool _pollingEnabled;
    private DateTimeOffset _nextPollAt;
    private readonly TaskCompletionSource _startupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RepoqlHost(
        CompositeFileSystem fileSystem,
        IndexingEngine engine,
        IOptions<RepoqlHostOptions> options,
        ILogger<RepoqlHost>? logger = null,
        IIndexingCoordinator? coordinator = null,
        IServiceDegradationTracker? degradation = null,
        IOperationManager? operationManager = null,
        UriRegistry? uriRegistry = null)
        : this(
            fileSystem,
            (artifact, enqueueOptions, token) => engine.EnqueueItemAsync(artifact, enqueueOptions, token),
            options,
            logger,
            coordinator,
            degradation,
            engine.Filter,
            operationManager,
            uriRegistry)
    {
        _engineLifetime = engine;
    }

    internal RepoqlHost(
        CompositeFileSystem fileSystem,
        Func<RawArtifact, IndexItemOptions, CancellationToken, Task> enqueue,
        IOptions<RepoqlHostOptions> options,
        ILogger<RepoqlHost>? logger = null,
        IIndexingCoordinator? coordinator = null,
        IServiceDegradationTracker? degradation = null,
        IUriFilter? filter = null,
        IOperationManager? operationManager = null,
        UriRegistry? uriRegistry = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _options = options?.Value ?? new RepoqlHostOptions();
        _logger = logger ?? NullLogger<RepoqlHost>.Instance;
        _coordinator = coordinator;
        _degradation = degradation;
        _filter = filter;
        _operationManager = operationManager;
        _uriRegistry = uriRegistry;
        _watchingEnabled = _options.EnableWatching;
    }

    /// <summary>
    /// Waits until the host has completed its startup sequence (full scan and/or watcher initialization).
    /// </summary>
    internal Task WaitForStartupAsync(CancellationToken cancellationToken = default)
        => _startupComplete.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Log mounted file systems at startup for debugging
        foreach (var (id, scheme, includeInEnum) in _fileSystem.GetMounts())
        {
            _logger.LogInformation(
                "RepoqlHost mount: id={MountId} scheme={Scheme} includeInEnum={IncludeInEnum}",
                id,
                scheme,
                includeInEnum);
        }

        if (_options.RunFullScanOnStartup)
        {
            try
            {
                await EnqueueFullScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RepoqlHost full scan failed; continuing with existing index.");
                _degradation?.MarkDegraded(ServiceDegradationKind.Indexer,
                    $"Indexer startup scan failed: {ex.Message}");
            }
        }

        if (_watchingEnabled)
        {
            try
            {
                await StartWatcherAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RepoqlHost watcher failed to start.");
                _degradation?.MarkDegraded(ServiceDegradationKind.Watcher,
                    $"Watcher failed to start: {ex.Message}");
                EnablePollingFallback();
            }
        }

        _startupComplete.TrySetResult();

        _dirtyScanLoop = Task.Run(() => DirtyScanLoopAsync(stoppingToken), CancellationToken.None);

        // Run git indexing synchronously after startup - waits for pipeline to become idle first
        if (_coordinator is not null)
        {
            try
            {
                await _coordinator.TriggerIncrementalGitIndexingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected when stopping
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Git history indexing failed");
            }
        }

        // Keep the background service alive until the host is asked to stop.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected when stopping
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;

        if (_watcherSubscription is not null)
        {
            _watcherSubscription.Dispose();
            _watcherSubscription = null;
        }

        if (_watcher is not null)
        {
            try
            {
                await _watcher.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop composite file system watcher.");
            }

            await _watcher.DisposeAsync().ConfigureAwait(false);
            _watcher = null;
        }

        await StopWatcherPumpAsync().ConfigureAwait(false);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_engineLifetime is not null)
        {
            await _engineLifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnqueueFullScanAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RepoqlHost starting full scan across mounted file systems.");
        var shouldTrack = _operationManager is not null && _uriRegistry is not null;
        var scope = shouldTrack ? new List<RepoUri>() : null;
        var shouldFilter = _options.DefaultIndexItemOptions.HasFlag(IndexItemOptions.OnlyIfNotExcluded);

        await foreach (var resource in _fileSystem.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_fileSystem.TryResolve(resource.Uri, out var store))
            {
                _logger.LogWarning("No file system registered for URI {Uri}", resource.Uri);
                continue;
            }

            if (!resource.File.Exists)
                continue;

            if (shouldTrack)
            {
                if (!shouldFilter || _filter is null || _filter.IncludeFile(resource.Uri))
                {
                    _uriRegistry!.TryRegisterDiscovered(resource.Uri);
                    scope!.Add(resource.Uri);
                }
            }

            var artifact = new RawArtifact(resource.File, store);
            await _enqueue(artifact, _options.DefaultIndexItemOptions, cancellationToken).ConfigureAwait(false);
        }

        if (shouldTrack)
        {
            var repoPath = RepoLocator.FindRepoRoot();
            _operationManager!.CreateOperation($"startup: {repoPath}", scope!);
        }
    }

    private async Task StartWatcherAsync(CancellationToken cancellationToken)
    {
        var capacity = _options.WatcherQueueCapacity <= 0 ? 10_000 : _options.WatcherQueueCapacity;
        _watcherChannel = Channel.CreateBounded<RawArtifact>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _watcherPump = Task.Run(() => PumpWatcherQueueAsync(_watcherChannel.Reader, cancellationToken), CancellationToken.None);

        _watcher = _fileSystem.WatchAll();
        _watcherSubscription = _watcher.Subscribe(new WatcherObserver(this));
        await _watcher.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("RepoqlHost change watcher started for all mounted file systems.");
    }

    private void EnablePollingFallback()
    {
        if (!_options.EnablePollingFallback)
            return;

        _watchingEnabled = false;
        _pollingEnabled = true;
        _nextPollAt = DateTimeOffset.UtcNow;
        _logger.LogWarning("RepoqlHost watcher disabled; falling back to polling every {Interval}.", _options.PollingInterval);
    }

    private void EnqueueWatcherArtifact(RawArtifact artifact, RepoUri uri)
    {
        var channel = _watcherChannel;
        if (channel is null)
            return;

        if (!channel.Writer.TryWrite(artifact) && !_isStopping)
        {
            _logger.LogWarning("Watcher queue is full; dropping change for {Uri}", uri);
        }
    }

    private async Task PumpWatcherQueueAsync(ChannelReader<RawArtifact> reader, CancellationToken stoppingToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var artifact))
                {
                    try
                    {
                        await EnqueueWithTrackingAsync(artifact, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Watcher pump failed to enqueue {Uri}", artifact.Uri);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task StopWatcherPumpAsync()
    {
        var channel = _watcherChannel;
        _watcherChannel = null;
        if (channel is not null)
        {
            channel.Writer.TryComplete();
        }

        var pump = _watcherPump;
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore, stopping
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watcher pump stopped with error.");
            }
        }
        _watcherPump = null;

        _dirtyTimer.Dispose();
        if (_dirtyScanLoop is not null)
        {
            try
            {
                await _dirtyScanLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class WatcherObserver : IObserver<ResourceChange>
    {
        private readonly RepoqlHost _host;

        public WatcherObserver(RepoqlHost host)
        {
            _host = host;
        }

        public void OnCompleted() { }

        public void OnError(Exception error)
        {
            if (error is InternalBufferOverflowException)
            {
                _host.MarkDirtyFromWatcher();
            }
            else
            {
                _host._logger.LogError(error, "File system watcher reported an error.");
            }
        }

        public void OnNext(ResourceChange value)
        {
            if (!_host._watchingEnabled)
                return;

            if (!value.File.Exists)
                return; // deletions are handled by pruners when idle.

            if (!_host._fileSystem.TryResolve(value.CurrentUri, out var store))
            {
                _host._logger.LogWarning("Watcher produced URI {Uri} but no mount matched.", value.CurrentUri);
                return;
            }

            var artifact = new RawArtifact(value.File, store);
            _host.EnqueueWatcherArtifact(artifact, value.CurrentUri);
            _host.UpdateLastWrite(value.CurrentUri, value.File.LastModified);
        }
    }

    private void MarkDirtyFromWatcher()
    {
        Volatile.Write(ref _dirty, true);
        _logger.LogInformation("File system watcher overflow detected; scheduling dirty scan.");
    }

    private void UpdateLastWrite(RepoUri uri, DateTimeOffset lastModified)
    {
        _lastWriteByUri[uri.AbsoluteUri] = lastModified;
    }

    private bool IsIndexerBusy() => Volatile.Read(ref _activeEnqueue) > 0;

    private async Task DirtyScanLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _dirtyTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (_pollingEnabled && DateTimeOffset.UtcNow >= _nextPollAt)
                {
                    if (!IsIndexerBusy())
                    {
                        _nextPollAt = DateTimeOffset.UtcNow.Add(_options.PollingInterval);
                        await RunDirtyScanAsync(stoppingToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (!_watchingEnabled)
                    continue;

                if (!Volatile.Read(ref _dirty))
                    continue;

                if (IsIndexerBusy())
                    continue;

                Volatile.Write(ref _dirty, false);
                await RunDirtyScanAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunDirtyScanAsync(CancellationToken cancellationToken)
    {
        var enqueued = 0;
        await foreach (var resource in _fileSystem.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!resource.File.Exists)
                continue;

            if (!_fileSystem.TryResolve(resource.Uri, out var store))
                continue;

            var lastModified = resource.File.LastModified;
            var key = resource.Uri.AbsoluteUri;

            if (_lastWriteByUri.TryGetValue(key, out var previous) && lastModified <= previous)
            {
                continue;
            }

            var artifact = new RawArtifact(resource.File, store);
            await EnqueueWithTrackingAsync(artifact, cancellationToken).ConfigureAwait(false);
            UpdateLastWrite(resource.Uri, lastModified);
            enqueued++;
        }

        _logger.LogDebug("Dirty scan completed. Enqueued {Count} artifacts.", enqueued);
    }

    private async Task EnqueueWithTrackingAsync(RawArtifact artifact, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeEnqueue);
        try
        {
            await _enqueue(artifact, _options.DefaultIndexItemOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeEnqueue);
        }
    }
}
