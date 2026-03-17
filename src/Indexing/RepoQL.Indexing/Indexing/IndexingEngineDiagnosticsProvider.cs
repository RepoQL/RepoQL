using RepoQL.Contracts.Diagnostics;
using RepoQL.Contracts.Embeddings;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.PostProcessing;

namespace RepoQL.Indexing.Indexing;

/// <summary>
/// Provides diagnostics for <see cref="IndexingEngine"/> without polluting the core class.
/// </summary>
public sealed class IndexingEngineDiagnosticsProvider(IndexingEngine engine) : IIndexingDiagnosticsProvider
{
    public IndexingDiagnosticsSnapshot GetSnapshot()
    {
        var hotPathSnapshot = engine.GetHotPathQueueSnapshot();
        var analysisSnapshot = engine.GetAnalysisQueueSnapshot();
        var idlePending = engine.GetPendingIdleProcessingCount();
        var idleActive = engine.ActiveIdleProcessingCount;
        var deferredPending = engine.GetPendingDeferredRetryItems().Count;
        var deferredActive = engine.GetDeferredRetryInFlightItems().Count;

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
            Epoch = engine.CurrentEpoch,
            HotPathDepth = hotPathSnapshot.Depth,
            HotPathActive = hotPathSnapshot.InProgress,
            IdlePending = Math.Max(0, idlePending),
            IdleActive = idleActive,
            AnalysisDepth = analysisSnapshot.Depth,
            AnalysisActive = analysisSnapshot.InProgress,
            DeferredRetryPending = deferredPending,
            DeferredRetryActive = deferredActive,
            DeferredToIdleCount = engine.DeferredToIdleCount,
            HotPathTimeouts = engine.HotPathTimeoutCount,
            AnalysisTimeouts = engine.AnalysisTimeoutCount,
            DeferredRetryTimeouts = engine.DeferredRetryTimeoutCount,
            WriterPending = 0, // DuckDbDataStore uses synchronous writes
            WriterTotal = 0, // DuckDbDataStore uses synchronous writes
            EmbedMode = engine.EmbeddingCoordinator is EmbeddingCoordinator ec
                ? ec.GetEmbeddingMode().ToString()
                : EmbeddingMode.None.ToString(),
            EmbedLastEpoch = engine.EmbeddingCoordinator is EmbeddingCoordinator ec2
                ? ec2.GetLastRefreshedEpoch()
                : 0,
            LastError = engine.LastError,
            ActiveWorkers = BuildWorkerSnapshot(),
        };
    }

    public IReadOnlyList<QueuedItemInfo> GetQueuedItems()
    {
        var items = new List<QueuedItemInfo>();

        items.AddRange(engine.GetHotPathPendingItems().Select(item => BuildQueuedItem(item, "HotPath", "queued")));
        items.AddRange(engine.GetHotPathInFlightItems().Select(info => BuildQueuedItem(
            info.Item,
            "HotPath",
            "processing",
            info.WorkerId,
            info.StartedAtUtc,
            info.Duration)));

        items.AddRange(engine.AnalysisQueue.GetPendingItems().Select(item => BuildQueuedItem(item, "Analysis", "queued")));
        items.AddRange(engine.GetAnalysisInFlightItems().Select(info => BuildQueuedItem(
            info.Item,
            "Analysis",
            "processing",
            info.WorkerId,
            info.StartedAtUtc,
            info.Duration)));

        items.AddRange(engine.GetPendingAnalysisItems().Select(item => BuildQueuedItem(item, "IdleProcessing", "queued")));

        items.AddRange(engine.GetPendingDeferredRetryItems().Select(item => BuildQueuedItem(item, "DeferredRetry", "deferred")));
        items.AddRange(engine.GetDeferredRetryInFlightItems().Select(info => BuildQueuedItem(
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

        workers.AddRange(engine.GetHotPathInFlightItems().Select(info => BuildWorkerInfo("HotPath", info)));
        workers.AddRange(engine.GetAnalysisInFlightItems().Select(info => BuildWorkerInfo("Analysis", info)));
        workers.AddRange(engine.GetDeferredRetryInFlightItems().Select(info => BuildWorkerInfo("DeferredRetry", info)));

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
