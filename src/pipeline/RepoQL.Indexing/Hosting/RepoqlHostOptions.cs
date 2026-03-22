using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Hosting;

public sealed class RepoqlHostOptions
{
    /// <summary>Whether to enumerate and enqueue every mounted file on startup.</summary>
    public bool RunFullScanOnStartup { get; set; } = true;

    /// <summary>Whether to watch mounted file systems for incremental changes.</summary>
    public bool EnableWatching { get; set; } = true;

    /// <summary>Enable polling fallback when watching fails to initialize.</summary>
    public bool EnablePollingFallback { get; set; } = true;

    /// <summary>Polling interval used when watcher fallback is active.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Indexing options used for any artifacts queued by the host.</summary>
    public IndexItemOptions DefaultIndexItemOptions { get; set; } = IndexItemOptions.Default;

    /// <summary>Maximum number of pending watcher events before dropping the oldest.</summary>
    public int WatcherQueueCapacity { get; set; } = 10_000;
}
