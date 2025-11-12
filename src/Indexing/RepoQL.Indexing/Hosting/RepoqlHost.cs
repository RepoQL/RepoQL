using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private readonly RepoqlHostOptions _options;
    private readonly ILogger<RepoqlHost> _logger;

    private IFileSystemWatcher? _watcher;
    private IDisposable? _watcherSubscription;

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
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
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
            await _enqueue(artifact, _options.DefaultIndexItemOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartWatcherAsync(CancellationToken cancellationToken)
    {
        _watcher = _fileSystem.WatchAll();
        _watcherSubscription = _watcher.Subscribe(new WatcherObserver(this));
        await _watcher.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("RepoqlHost change watcher started for all mounted file systems.");
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
            _ = _host._enqueue(artifact, _host._options.DefaultIndexItemOptions, CancellationToken.None);
        }
    }
}
