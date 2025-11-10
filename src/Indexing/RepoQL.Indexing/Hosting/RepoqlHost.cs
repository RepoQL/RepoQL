using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Hosting;

/// <summary>
/// Background service that orchestrates enumerating and watching repositories to feed the indexing engine.
/// </summary>
public sealed class RepoqlHost : BackgroundService
{
    private readonly CompositeFileSystem _fileSystem;
    private readonly IIndexingWorkScheduler _scheduler;
    private readonly RepoqlHostOptions _options;
    private readonly ILogger<RepoqlHost> _logger;
    private readonly Channel<EnqueueRequest> _queue;

    private IFileSystemWatcher? _watcher;
    private IDisposable? _watcherSubscription;
    private bool _channelCompleted;

    public RepoqlHost(
        CompositeFileSystem fileSystem,
        IIndexingWorkScheduler scheduler,
        IOptions<RepoqlHostOptions> options,
        ILogger<RepoqlHost>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options?.Value ?? new RepoqlHostOptions();
        _logger = logger ?? NullLogger<RepoqlHost>.Instance;
        _queue = Channel.CreateUnbounded<EnqueueRequest>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processingTask = ProcessQueueAsync(stoppingToken);

        if (_options.RunFullScanOnStartup)
        {
            await EnqueueFullScanAsync(stoppingToken).ConfigureAwait(false);
        }

        if (_options.EnableWatching)
        {
            await StartWatcherAsync(stoppingToken).ConfigureAwait(false);
        }

        await processingTask.ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CompleteChannel();

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

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
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
            await WriteAsync(new EnqueueRequest(artifact, _options.DefaultIndexItemOptions), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartWatcherAsync(CancellationToken cancellationToken)
    {
        _watcher = _fileSystem.WatchAll();
        _watcherSubscription = _watcher.Subscribe(new WatcherObserver(this));
        await _watcher.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("RepoqlHost change watcher started for all mounted file systems.");
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var work))
                {
                    try
                    {
                        await _scheduler.EnqueueAsync(work.Artifact, work.Options, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to enqueue artifact {Uri}.", work.Artifact.Uri);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private ValueTask WriteAsync(EnqueueRequest request, CancellationToken cancellationToken)
    {
        if (_queue.Writer.TryWrite(request))
            return ValueTask.CompletedTask;

        return _queue.Writer.WriteAsync(request, cancellationToken);
    }

    private void TryWriteFromWatcher(EnqueueRequest request)
    {
        if (_queue.Writer.TryWrite(request))
            return;

        _ = _queue.Writer.WriteAsync(request, CancellationToken.None).AsTask();
    }

    private void CompleteChannel()
    {
        if (_channelCompleted)
            return;

        _channelCompleted = true;
        _queue.Writer.TryComplete();
    }

    private readonly record struct EnqueueRequest(RawArtifact Artifact, IndexItemOptions Options);

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
            _host.TryWriteFromWatcher(new EnqueueRequest(artifact, _host._options.DefaultIndexItemOptions));
        }
    }
}
