using Microsoft.Extensions.Logging;

namespace RepoQL.Core.Configuration;

/// <summary>
/// Purpose: Polls config files on disk and triggers <see cref="ResolvedConfig"/> reloads on changes.
/// Complexity: Tracks a small fixed set of config files with periodic timestamp checks and debounced reloads.
/// </summary>
internal sealed class ConfigFilePoller : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly ILogger? _logger;
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, FileStamp> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _pollTimer;
    private readonly Timer _debounceTimer;
    private volatile int _disposed;

    private const int PollMs = 3000;
    private const int DebounceMs = 300;

    public ConfigFilePoller(ResolvedConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new Timer(OnPollElapsed, null, PollMs, PollMs);

        AddPath(Path.Combine(config.UserConfigDir, "config.json"));

        if (config.RepoRoot is not null)
        {
            AddPath(Path.Combine(config.RepoRoot, ".repoql.json"));
            AddPath(Path.Combine(config.RepoRoot, ".repoql", "config.json"));
        }
    }

    private void AddPath(string path)
    {
        _paths.Add(path);
        _lastSeen[path] = ReadStamp(path);
    }

    private void OnPollElapsed(object? state)
    {
        if (_disposed != 0)
            return;

        try
        {
            var changed = false;
            foreach (var path in _paths)
            {
                var current = ReadStamp(path);
                if (_lastSeen.TryGetValue(path, out var previous) && previous.Equals(current))
                    continue;

                _lastSeen[path] = current;
                changed = true;
            }

            if (changed)
                _debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to poll configuration files");
        }
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

    private static FileStamp ReadStamp(string path)
    {
        try
        {
            if (!File.Exists(path))
                return FileStamp.Missing;

            return new FileStamp(true, File.GetLastWriteTimeUtc(path));
        }
        catch
        {
            return FileStamp.Missing;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _pollTimer.Dispose();
        _debounceTimer.Dispose();
        _paths.Clear();
        _lastSeen.Clear();
    }

    private readonly record struct FileStamp(bool Exists, DateTime LastWriteTimeUtc)
    {
        public static FileStamp Missing => new(false, DateTime.MinValue);
    }
}
