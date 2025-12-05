using RepoQL.Contracts.Diagnostics;
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
        var writerStatus = _engine.Writer?.GetStatus();

        var status = ComputeStatus(hotPathSnapshot, analysisSnapshot, idlePending, idleActive);

        return new IndexingDiagnosticsSnapshot
        {
            Status = status,
            Epoch = _engine.CurrentEpoch,
            HotPathDepth = hotPathSnapshot.Depth,
            HotPathActive = hotPathSnapshot.InProgress,
            IdlePending = idlePending - idleActive,
            IdleActive = idleActive,
            AnalysisDepth = analysisSnapshot.Depth,
            AnalysisActive = analysisSnapshot.InProgress,
            WriterPending = writerStatus?.PendingCount ?? 0,
            WriterTotal = writerStatus?.TotalProcessed ?? 0,
            EmbedEnabled = _engine.VectorCoordinator is not NullVectorIndexCoordinator,
            EmbedLastEpoch = _engine.VectorCoordinator is VectorIndexCoordinator vic
                ? vic.GetLastRefreshedEpoch()
                : 0,
            LastError = _engine.LastError
        };
    }

    public IReadOnlyList<QueuedItemInfo> GetQueuedItems()
    {
        var items = new List<QueuedItemInfo>();

        // Hot path queue items
        var hotPathSnapshot = _engine.GetHotPathQueueSnapshot();
        foreach (var item in _engine.GetHotPathPendingItems())
        {
            items.Add(new QueuedItemInfo
            {
                Uri = item.Uri.ToString(),
                Name = item.Name,
                Stage = "HotPath",
                Status = hotPathSnapshot.InProgress > 0 ? "processing" : "queued",
                Epoch = item.Epoch,
                MimeType = item.MediaType?.ToString() ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString(),
                Size = item.Length,
                ReadOnly = item.IsReadOnly
            });
        }

        // Analysis queue items
        var analysisSnapshot = _engine.GetAnalysisQueueSnapshot();
        foreach (var item in _engine.AnalysisQueue.GetPendingItems())
        {
            items.Add(new QueuedItemInfo
            {
                Uri = item.Uri.ToString(),
                Name = item.Name,
                Stage = "Analysis",
                Status = analysisSnapshot.InProgress > 0 ? "processing" : "queued",
                Epoch = item.Epoch,
                MimeType = item.MediaType?.ToString() ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString(),
                Size = item.Length,
                ReadOnly = item.IsReadOnly
            });
        }

        // Pending idle processing items
        var idleItems = _engine.GetPendingAnalysisItems();
        var activeIdle = _engine.ActiveIdleProcessingCount;
        foreach (var item in idleItems)
        {
            items.Add(new QueuedItemInfo
            {
                Uri = item.Uri.ToString(),
                Name = item.Name,
                Stage = "IdleProcessing",
                Status = activeIdle > 0 ? "processing" : "queued",
                Epoch = item.Epoch,
                MimeType = item.MediaType?.ToString() ?? item.RawArtifact.ProvisionalMediaType.Value?.ToString(),
                Size = item.Length,
                ReadOnly = item.IsReadOnly
            });
        }

        return items;
    }

    private static string ComputeStatus(
        WorkQueueSnapshot hotPath,
        WorkQueueSnapshot analysis,
        int idlePending,
        int idleActive)
    {
        if (hotPath.Depth > 0 || hotPath.InProgress > 0)
            return "indexing";
        if (idleActive > 0 || idlePending > 0)
            return "idle_processing";
        if (analysis.Depth > 0 || analysis.InProgress > 0)
            return "analyzing";
        return "idle";
    }
}
