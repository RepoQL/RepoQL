namespace RepoQL.Contracts.Diagnostics;

/// <summary>
/// Interface for components that can provide indexing diagnostics.
/// </summary>
public interface IIndexingDiagnosticsProvider
{
    IndexingDiagnosticsSnapshot GetSnapshot();
    IReadOnlyList<QueuedItemInfo> GetQueuedItems();
}

/// <summary>
/// Information about an item in one of the indexing queues.
/// </summary>
public record QueuedItemInfo
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public required string Stage { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public required long Epoch { get; init; }
    public required string? MimeType { get; init; }
    public required long Size { get; init; }
    public required bool ReadOnly { get; init; }
}

/// <summary>
/// A point-in-time snapshot of indexing state with flat structure for easy consumption.
/// </summary>
public record IndexingDiagnosticsSnapshot
{
    /// <summary>Overall status: "idle", "indexing", "analyzing", or "busy".</summary>
    public required string Status { get; init; }

    /// <summary>Current epoch number (monotonically increasing).</summary>
    public required long Epoch { get; init; }

    /// <summary>Number of items in hot path queue (pending + in-flight).</summary>
    public required int HotPathDepth { get; init; }

    /// <summary>Number of workers currently processing hot path items.</summary>
    public required int HotPathActive { get; init; }

    /// <summary>Number of items waiting for idle post-processing.</summary>
    public required int IdlePending { get; init; }

    /// <summary>Number of epochs currently being processed in idle phase.</summary>
    public required int IdleActive { get; init; }

    /// <summary>Number of items in multi-file analysis queue.</summary>
    public required int AnalysisDepth { get; init; }

    /// <summary>Number of workers currently processing analysis items.</summary>
    public required int AnalysisActive { get; init; }

    /// <summary>Number of write operations pending in writer queue.</summary>
    public required int WriterPending { get; init; }

    /// <summary>Total write operations processed since startup.</summary>
    public required long WriterTotal { get; init; }

    /// <summary>Embedding mode: None, StructureOnly, or Full.</summary>
    public required string EmbedMode { get; init; }

    /// <summary>Last epoch for which embeddings were refreshed.</summary>
    public required long EmbedLastEpoch { get; init; }

    /// <summary>Last error message from indexing, or null if no recent error.</summary>
    public required string? LastError { get; init; }
}

/// <summary>
/// Static accessor for diagnostics that can be used by UDFs.
/// </summary>
public static class IndexingDiagnostics
{
    private static IIndexingDiagnosticsProvider? _provider;

    /// <summary>
    /// Registers the diagnostics provider. Should be called once during startup.
    /// </summary>
    public static void SetProvider(IIndexingDiagnosticsProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Gets the current diagnostics snapshot as key-value text (no JSON, survives IL trimming).
    /// Embedding provider info is passed directly rather than stored statically.
    /// </summary>
    public static string GetDiagnosticsText(string? queryEmbedProvider, bool queryEmbedEnabled, string? queryEmbedModel)
    {
        if (_provider is null)
            return "error: No diagnostics provider registered";

        var snapshot = _provider.GetSnapshot();

        // Use key-value format instead of JSON to survive IL trimming
        return string.Join("\n",
            $"status: {snapshot.Status}",
            $"epoch: {snapshot.Epoch}",
            $"hot_path_depth: {snapshot.HotPathDepth}",
            $"hot_path_active: {snapshot.HotPathActive}",
            $"idle_pending: {snapshot.IdlePending}",
            $"idle_active: {snapshot.IdleActive}",
            $"analysis_depth: {snapshot.AnalysisDepth}",
            $"analysis_active: {snapshot.AnalysisActive}",
            $"writer_pending: {snapshot.WriterPending}",
            $"writer_total: {snapshot.WriterTotal}",
            $"embed_mode: {snapshot.EmbedMode}",
            $"embed_last_epoch: {snapshot.EmbedLastEpoch}",
            $"last_error: {snapshot.LastError ?? "null"}",
            $"query_embed_provider: {queryEmbedProvider ?? "null"}",
            $"query_embed_enabled: {queryEmbedEnabled}",
            $"query_embed_model: {queryEmbedModel ?? "null"}"
        );
    }

    /// <summary>
    /// Gets the current diagnostics snapshot.
    /// </summary>
    public static IndexingDiagnosticsSnapshot? GetSnapshot() => _provider?.GetSnapshot();

    /// <summary>
    /// Gets information about all queued items.
    /// </summary>
    public static IReadOnlyList<QueuedItemInfo> GetQueuedItems()
        => _provider?.GetQueuedItems() ?? Array.Empty<QueuedItemInfo>();
}
