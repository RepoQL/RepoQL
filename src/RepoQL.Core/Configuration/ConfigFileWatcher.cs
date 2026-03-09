using Microsoft.Extensions.Logging;

namespace RepoQL.Core.Configuration;

/// <summary>
/// Purpose: Watches config files on disk and triggers <see cref="ResolvedConfig.Reload()"/> on changes.
/// Complexity: Multiple FileSystemWatchers (user, repo, local scopes), debounced to coalesce rapid saves.
/// </summary>
internal sealed class ConfigFileWatcher : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly ILogger? _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounceTimer;
    private volatile int _disposed;

    private const int DebounceMs = 300;

    public ConfigFileWatcher(ResolvedConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        WatchFile(Path.Combine(config.UserConfigDir, "config.json"));

        if (config.RepoRoot is not null)
        {
            WatchFile(Path.Combine(config.RepoRoot, ".repoql.json"));
            WatchFile(Path.Combine(config.RepoRoot, ".repoql", "config.json"));
        }
    }

    private void WatchFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);

        if (directory is null || !Directory.Exists(directory))
            return;

        try
        {
            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not watch config file {Path}", filePath);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Reset debounce timer — coalesces multiple events from a single save
        _debounceTimer.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed != 0)
            return;

        try
        {
            _config.Reload(_logger);
            _logger?.LogInformation("Configuration reloaded from disk");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to reload configuration from disk");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _debounceTimer.Dispose();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
