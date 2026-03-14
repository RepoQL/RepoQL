using RepoQL.Contracts.Diagnostics;
using RepoQL.Contracts.Embeddings;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.PostProcessing;

namespace RepoQL.Indexing.Indexing;

/// <summary>
/// Provides diagnostics for <see cref="IndexingEngine"/> without polluting the core class.
/// </summary>
public sealed class IndexingEngineDiagnosticsProvider : IIndexingDiagnosticsProvider
{
    private readonly IndexingEngine _engine;

    public IndexingEngineDiagnosticsProvider(IndexingEngine engine)
    {
        _engine = engine;
    }

    public IndexingDiagnosticsSnapshot GetSnapshot()
    {
        var hotPathSnapshot = _engine.GetHotPathQueueSnapshot();
        var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();
        var idlePending = _engine.GetPendingIdleProcessingCount();
        var idleActive = _engine.ActiveIdleProcessingCount;
        var deferredPending = _engine.GetPendingDeferredRetryItems().Count;
        var deferredActive = _engine.GetDeferredRetryInFlightItems().Count;

        var status = ComputeStatus(
            hotPathSnapshot,
            analysisSnapshot,
            idlePending,
            idleActive,
            deferredPending,
            deferredActive);

        return new IndexingDiagnosticsSnapshot
        {
            Status = status,
            Epoch = _engine.CurrentEpoch,
            HotPathDepth = hotPathSnapshot.Depth,
            HotPathActive = hotPathSnapshot.InProgress,
            IdlePending = Math.Max(0, idlePending),
            IdleActive = idleActive,
            AnalysisDepth = analysisSnapshot.Depth,
            AnalysisActive = analysisSnapshot.InProgress,
            DeferredRetryPending = deferredPending,
            DeferredRetryActive = deferredActive,
            DeferredToIdleCount = _engine.DeferredToIdleCount,
            HotPathTimeouts = _engine.HotPathTimeoutCount,
            AnalysisTimeouts = _engine.AnalysisTimeoutCount,
            DeferredRetryTimeouts = _engine.DeferredRetryTimeoutCount,
            WriterPending = 0, // DuckDbDataStore uses synchronous writes
            WriterTotal = 0, // DuckDbDataStore uses synchronous writes
            EmbedMode = _engine.VectorCoordinator is VectorIndexCoordinator vic
                ? vic.GetEmbeddingMode().ToString()
                : EmbeddingMode.None.ToString(),
            EmbedLastEpoch = _engine.VectorCoordinator is VectorIndexCoordinator vic2
                ? vic2.GetLastRefreshedEpoch()
                : 0,
            LastError = _engine.LastError,
            ActiveWorkers = BuildWorkerSnapshot(),
        };
    }

    public IReadOnlyList<QueuedItemInfo> GetQueuedItems()
    {
        var items = new List<QueuedItemInfo>();

        items.AddRange(_engine.GetHotPathPendingItems().Select(item => BuildQueuedItem(item, "HotPath", "queued")));
        items.AddRange(_engine.GetHotPathInFlightItems().Select(info => BuildQueuedItem(
            info.Item,
            "HotPath",
            "processing",
            info.WorkerId,
            info.StartedAtUtc,
            info.Duration)));

        items.AddRange(_engine.AnalysisQueue.GetPendingItems().Select(item => BuildQueuedItem(item, "Analysis", "queued")));
        items.AddRange(_engine.GetAnalysisInFlightItems().Select(info => BuildQueuedItem(
            info.Item,
            "Analysis",
            "processing",
            info.WorkerId,
            info.StartedAtUtc,
            info.Duration)));

        items.AddRange(_engine.GetPendingAnalysisItems().Select(item => BuildQueuedItem(item, "IdleProcessing", "queued")));

        items.AddRange(_engine.GetPendingDeferredRetryItems().Select(item => BuildQueuedItem(item, "DeferredRetry", "deferred")));
        items.AddRange(_engine.GetDeferredRetryInFlightItems().Select(info => BuildQueuedItem(
            info.Item,
            "DeferredRetry",
            "retrying",
            info.WorkerId,
            info.StartedAtUtc,
            info.Duration)));

        return items;
    }

    private IReadOnlyList<IndexingWorkerInfo> BuildWorkerSnapshot()
    {
        var workers = new List<IndexingWorkerInfo>();

        workers.AddRange(_engine.GetHotPathInFlightItems().Select(info => BuildWorkerInfo("HotPath", info)));
        workers.AddRange(_engine.GetAnalysisInFlightItems().Select(info => BuildWorkerInfo("Analysis", info)));
        workers.AddRange(_engine.GetDeferredRetryInFlightItems().Select(info => BuildWorkerInfo("DeferredRetry", info)));

        return workers;
    }

    private static IndexingWorkerInfo BuildWorkerInfo(string queue, WorkQueueInFlightItem<IndexItem> info)
    {
        return new IndexingWorkerInfo
        {
            Queue = queue,
            WorkerId = info.WorkerId,
            Uri = info.Item.Uri.ToString(),
            Name = info.Item.Name,
            Stage = ResolveStage(info.Item, queue),
            StartedAt = info.StartedAtUtc,
            ElapsedMs = info.Duration.TotalMilliseconds,
            TimeoutAttempts = info.Item.TimeoutAttempts,
            DeferredRetry = info.Item.IsDeferredRetry,
        };
    }

    private static QueuedItemInfo BuildQueuedItem(
        IndexItem item,
        string queue,
        string status,
        int? workerId = null,
        DateTimeOffset? startedAt = null,
        TimeSpan? elapsed = null)
    {
        return new QueuedItemInfo
        {
            Uri = item.Uri.ToString(),
            Name = item.Name,
            Stage = ResolveStage(item, queue),
            Status = status,
            EnqueuedAt = item.CreatedAt,
            Epoch = item.Epoch,
            MimeType = item.MediaType?.ToString() ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString(),
            Size = item.Length,
            ReadOnly = item.IsReadOnly,
            WorkerId = workerId,
            StartedAt = startedAt,
            ElapsedMs = elapsed?.TotalMilliseconds,
            TimeoutAttempts = item.TimeoutAttempts,
            DeferredRetry = item.IsDeferredRetry,
        };
    }

    private static string ResolveStage(IndexItem item, string queue)
    {
        if (!string.IsNullOrWhiteSpace(item.CurrentOperation))
            return item.CurrentOperation;

        return queue;
    }

    private static string ComputeStatus(
        WorkQueueSnapshot hotPath,
        WorkQueueSnapshot analysis,
        int idlePending,
        int idleActive,
        int deferredPending,
        int deferredActive)
    {
        if (hotPath.Depth > 0)
            return "indexing";
        if (idleActive > 0 || idlePending > 0)
            return "idle_processing";
        if (deferredActive > 0 || deferredPending > 0)
            return "idle_processing";
        if (analysis.Depth > 0)
            return "analyzing";
        return "idle";
    }
}
