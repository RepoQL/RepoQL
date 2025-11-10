using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Hosting;

public sealed class RepoqlHostOptions
{
    /// <summary>Whether to enumerate and enqueue every mounted file on startup.</summary>
    public bool RunFullScanOnStartup { get; set; } = true;

    /// <summary>Whether to watch mounted file systems for incremental changes.</summary>
    public bool EnableWatching { get; set; } = true;

    /// <summary>Indexing options used for any artifacts queued by the host.</summary>
    public IndexItemOptions DefaultIndexItemOptions { get; set; } = IndexItemOptions.Default;
}
