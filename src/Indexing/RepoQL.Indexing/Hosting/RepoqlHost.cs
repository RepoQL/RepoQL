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
    private readonly RepoqlHostOptions _options;
    private readonly ILogger<RepoqlHost> _logger;

    private IFileSystemWatcher? _watcher;
    private IDisposable? _watcherSubscription;
    private Channel<RawArtifact>? _watcherChannel;
    private Task? _watcherPump;
    private volatile bool _isStopping;

    public RepoqlHost(
        CompositeFileSystem fileSystem,
        IndexingEngine engine,
        IOptions<RepoqlHostOptions> options,
        ILogger<RepoqlHost>? logger = null)
        : this(
            fileSystem,
            (artifact, enqueueOptions, token) => engine.EnqueueItemAsync(artifact, enqueueOptions, token),
            options,
            logger)
    {
        _engineLifetime = engine;
    }

    internal RepoqlHost(
        CompositeFileSystem fileSystem,
        Func<RawArtifact, IndexItemOptions, CancellationToken, Task> enqueue,
        IOptions<RepoqlHostOptions> options,
        ILogger<RepoqlHost>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _options = options?.Value ?? new RepoqlHostOptions();
        _logger = logger ?? NullLogger<RepoqlHost>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RunFullScanOnStartup)
        {
            await EnqueueFullScanAsync(stoppingToken).ConfigureAwait(false);
        }

        if (_options.EnableWatching)
        {
            await StartWatcherAsync(stoppingToken).ConfigureAwait(false);
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
        await foreach (var resource in _fileSystem.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_fileSystem.TryResolve(resource.Uri, out var store))
            {
                _logger.LogWarning("No file system registered for URI {Uri}", resource.Uri);
                continue;
            }

            if (!resource.File.Exists)
                continue;

            var artifact = new RawArtifact(resource.File, store);
            await _enqueue(artifact, _options.DefaultIndexItemOptions, cancellationToken).ConfigureAwait(false);
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
                        await _enqueue(artifact, _options.DefaultIndexItemOptions, stoppingToken).ConfigureAwait(false);
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
            _host._logger.LogError(error, "File system watcher reported an error.");
        }

        public void OnNext(ResourceChange value)
        {
            if (!_host._options.EnableWatching)
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
        }
    }
}
